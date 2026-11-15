// The development client. It forwards the diagnostics and connection state of `@runic-artifex/views` to the
// development server, which keeps them on its timeline, reports each View's state and superseded command replies to
// the state inspector, sends each settled Bridge call to the dock's call timeline and draws it on a "Runic" track of
// the browser's Performance panel, sends the spans of Bridge calls for the server's OTLP export when it has one, warns
// about script that overwrites a focused text field with an older value (`stale-input`), adds Runic Inspect
// (Alt+Shift+X, inspect.ts), and loads the Vite DevTools dock where the document does not (`loadDock`). It has no
// exports.
//
// `runic()` injects it into served pages through `virtual:runic/client`, and loads it through that module for an
// application import of `@runic-artifex/vite-plugin-runic/client`; the build replaces it with an empty module. A
// development server without `runic()`, such as the Angular CLI's, serves the package itself: the page imports it
// in development only, and it reports to `runicDevSessionProxy()` (see channel.ts).
import type { RunicClientOptions } from "./channel.js";
import {
  callEvent,
  clientOptionsKey,
  connectionEvent,
  diagnosticEvent,
  diagnosticsPath,
  maximumClientMessageBytes,
  spanEvent,
  viewEvent,
} from "./channel.js";
import { sanitizeDiagnosticSummary, sanitizeViewState } from "./diagnostics.js";
import { sanitizeCallReport } from "./calls.js";
import { installStaleInputWarning } from "./stale-input.js";
import { installInspect } from "./inspect.js";

interface ViteHotContextLike {
  readonly data: Record<string, unknown>;
  send(event: string, data?: unknown): void;
}

interface ImportMetaWithHot extends ImportMeta {
  readonly hot?: ViteHotContextLike;
}

type ClientHost = Record<symbol, unknown> & {
  fetch?: (input: string, init: Record<string, unknown>) => Promise<{ readonly status: number }>;
  TextEncoder?: new () => { encode(text: string): { readonly byteLength: number } };
  addEventListener?: (type: string, listener: () => void) => void;
  removeEventListener?: (type: string, listener: () => void) => void;
};

const host = globalThis as unknown as ClientHost;
const hot = (import.meta as ImportMetaWithHot).hot;
const options = host[clientOptionsKey] as RunicClientOptions | undefined;
// The listeners of a previous evaluation (HMR), so this copy replaces them.
const data: Record<string, unknown> = hot?.data ?? {};

// Messages wait in order, one request at a time, so a later connection state never overtakes an earlier one.
const maximumPendingMessages = 100;
const pending: string[] = [];
let posting = false;
// `open` after the endpoint first answered, `closed` after it did not: then the page has no Runic development server
// (a build that kept the client, a server without the endpoint), and the client stops.
let channel: "unknown" | "open" | "closed" = "unknown";

function post(event: string, payload: unknown): void {
  if (channel === "closed") return;
  const body = JSON.stringify({ event, data: payload });
  const encoder = host.TextEncoder;
  const size = encoder ? new encoder().encode(body).byteLength : body.length * 3;
  if (size > maximumClientMessageBytes) return;
  pending.push(body);
  if (pending.length > maximumPendingMessages) pending.shift();
  if (!posting) void drain();
}

async function drain(): Promise<void> {
  const fetch = host.fetch;
  posting = true;
  try {
    while (pending.length > 0 && channel !== "closed") {
      const body = pending.shift() as string;
      let answered = false;
      try {
        const response = await fetch?.(diagnosticsPath, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
          credentials: "same-origin",
          cache: "no-store",
        });
        answered = response?.status === 204;
      } catch { /* answered stays false */ }
      if (!answered) {
        close();
        return;
      }
      if (channel === "unknown") {
        channel = "open";
        // Without Vite's HMR context only the answer shows that a development server serves the page.
        if (!hot) installStaleInputWarning(globalThis);
      }
    }
  } finally {
    posting = false;
  }
}

const send: (event: string, payload: unknown) => void = hot && options?.channel === "hot"
  ? (event, payload) => hot.send(event, payload)
  : post;

function sendDiagnostic(candidate: unknown): void {
  const summary = sanitizeDiagnosticSummary(candidate);
  if (summary) send(diagnosticEvent, summary);
}

// @runic-artifex/views publishes runtime failures on a page-wide listener set
// under this registered symbol, so this client needs no dependency on it.
const viewsDiagnosticsKey = Symbol.for("runic.views.diagnostics");
const viewsListenerKey = "runic:views-listener";

interface ViewsDiagnostic {
  readonly kind?: unknown;
  readonly code?: unknown;
  readonly message?: unknown;
  readonly route?: unknown;
  readonly detail?: unknown;
  readonly member?: unknown;
  readonly requestId?: unknown;
}

// The error class a code comes from, for a failure without exception detail.
function fallbackType(code: string): string {
  if (code === "reported") return "Error";
  if (code === "uncertain") return "BridgeOperationUncertainError";
  if (code === "stale-input") return "StaleInputWarning";
  return "BridgeError";
}

/** Forwards a Views runtime diagnostic to the timeline. */
function reportViewsDiagnostic(diagnostic: ViewsDiagnostic): void {
  const route = typeof diagnostic.route === "string" ? diagnostic.route.slice(0, 120) : undefined;
  const code = typeof diagnostic.code === "string" ? diagnostic.code.slice(0, 32) : "error";
  const message = typeof diagnostic.message === "string" ? diagnostic.message : "";
  const error = diagnostic.kind !== "operation";
  const member = typeof diagnostic.member === "string" ? diagnostic.member.slice(0, 80) : undefined;
  const requestId = typeof diagnostic.requestId === "string" ? diagnostic.requestId.slice(0, 80) : undefined;
  // The label stays short and path-free, so the timeline never redacts it.
  // An error's message, which may name files or packages, goes to the
  // failure, whose bounds keep paths.
  sendDiagnostic({
    source: "views",
    kind: error ? "error" : "operation",
    label: `${member ?? route ?? "Bridge"} ${code}`,
    detail: {
      code, ...(route ? { route } : {}), ...(member ? { member } : {}), ...(requestId ? { requestId } : {}),
      ...(error ? {} : { message }),
    },
    ...(error ? { failure: diagnostic.detail ?? { type: fallbackType(code), message } } : {}),
  });
}

function viewsDiagnostics(): Set<(diagnostic: ViewsDiagnostic) => void> {
  const listeners = host as Record<symbol, Set<(diagnostic: ViewsDiagnostic) => void> | undefined>;
  return listeners[viewsDiagnosticsKey] ??= new Set();
}

function connectViewsDiagnostics(): void {
  const listeners = viewsDiagnostics();
  // HMR re-evaluates this module; replace the listener of the previous copy.
  const previous = data[viewsListenerKey] as ((diagnostic: ViewsDiagnostic) => void) | undefined;
  if (previous) listeners.delete(previous);
  listeners.add(reportViewsDiagnostic);
  data[viewsListenerKey] = reportViewsDiagnostic;
}

// @runic-artifex/views publishes the page's connection state on this channel once a View
// connects, or from page load when this client asks for it with `requested` and `start`
// (specs/application, "Shared globals").
const viewsConnectionKey = Symbol.for("runic.views.connection");
const connectionListenerKey = "runic:connection-listener";

interface ViewsConnectionChannel {
  readonly listeners: Set<(state: string) => void>;
  state?: string;
  requested?: boolean;
  start?: () => void;
}

function reportConnection(state: string): void {
  send(connectionEvent, { state });
}

function viewsConnection(): ViewsConnectionChannel {
  const channels = host as Record<symbol, ViewsConnectionChannel | undefined>;
  return channels[viewsConnectionKey] ??= { listeners: new Set() };
}

function connectViewsConnection(): void {
  const connection = viewsConnection();
  const previous = data[connectionListenerKey] as ((state: string) => void) | undefined;
  if (previous) connection.listeners.delete(previous);
  connection.listeners.add(reportConnection);
  data[connectionListenerKey] = reportConnection;
  // The first report also opens the HTTP channel, so it is sent even before a View has a state.
  if (typeof connection.state === "string" || send === post) reportConnection(connection.state ?? "unknown");
  // Observe from page load, not only once a View connects: a copy of @runic-artifex/views that
  // loads later sees `requested`, and one that already loaded has set `start`.
  connection.requested = true;
  if (typeof connection.start === "function") connection.start();
}

// The state inspector. @runic-artifex/views 0.15 or later reports to this channel once a reader set `requested`
// (specs/application, "Shared globals"): each route's state as its presentations receive it, a closed route, and a
// command the producer answered `superseded`. The client sends a route's latest state at most every 250 ms.
const viewsInspectorKey = Symbol.for("runic.views.inspector");
const inspectorListenerKey = "runic:inspector-listener";
const pageHideListenerKey = "runic:page-hide-listener";
const pageKey = "runic:page";
const stateInterval = 250;

type InspectorEvent =
  | { readonly kind: "state"; readonly route: string; readonly revision?: number; readonly epoch?: number;
      readonly presentations: number; readonly state: unknown }
  | { readonly kind: "closed"; readonly route: string }
  | { readonly kind: "superseded"; readonly route: string; readonly member: string; readonly epoch?: number;
      readonly revision?: number };

interface ViewsInspectorChannel {
  readonly listeners: Set<(event: InspectorEvent) => void>;
  requested?: boolean;
}

// Several windows can share one development server, so the server keeps Views per page.
const page = typeof data[pageKey] === "string" ? data[pageKey] as string : (data[pageKey] = randomPage()) as string;
const pendingStates = new Map<string, Extract<InspectorEvent, { kind: "state" }>>();
let stateTimer: ReturnType<typeof setTimeout> | undefined;

function randomPage(): string {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`.slice(0, 32);
}

function sendStates(): void {
  stateTimer = undefined;
  for (const event of pendingStates.values()) {
    send(viewEvent, {
      page, route: event.route, revision: event.revision, epoch: event.epoch, presentations: event.presentations,
      // Sanitized here too, so that the state leaves the page bounded and redacted.
      state: sanitizeViewState(event.state),
    });
  }
  pendingStates.clear();
}

function reportInspector(event: InspectorEvent): void {
  if (typeof event !== "object" || event === null || typeof event.route !== "string") return;
  if (event.kind === "state") {
    pendingStates.set(event.route, event);
    stateTimer ??= setTimeout(sendStates, stateInterval);
  } else if (event.kind === "closed") {
    pendingStates.delete(event.route);
    send(viewEvent, { page, route: event.route, closed: true });
  } else if (event.kind === "superseded") {
    // The call resolved like one that ran, so only the timeline shows that nothing ran.
    sendDiagnostic({
      source: "views",
      kind: "command",
      label: `${String(event.member).slice(0, 80)} superseded`,
      detail: {
        code: "superseded", route: event.route.slice(0, 120), member: String(event.member).slice(0, 80),
        ...(typeof event.epoch === "number" ? { epoch: event.epoch } : {}),
        ...(typeof event.revision === "number" ? { revision: event.revision } : {}),
      },
    });
  }
}

function viewsInspector(): ViewsInspectorChannel {
  const channels = host as Record<symbol, ViewsInspectorChannel | undefined>;
  return channels[viewsInspectorKey] ??= { listeners: new Set() };
}

function pageHidden(): void {
  pendingStates.clear();
  send(viewEvent, { page, closed: true });
}

function connectViewsInspector(): void {
  const inspector = viewsInspector();
  const previous = data[inspectorListenerKey] as ((event: InspectorEvent) => void) | undefined;
  if (previous) inspector.listeners.delete(previous);
  inspector.listeners.add(reportInspector);
  data[inspectorListenerKey] = reportInspector;
  inspector.requested = true;
  // The page's Views go away with it; the server also keeps only the 32 most recent.
  const previousHide = data[pageHideListenerKey] as (() => void) | undefined;
  if (previousHide) host.removeEventListener?.("pagehide", previousHide);
  host.addEventListener?.("pagehide", pageHidden);
  data[pageHideListenerKey] = pageHidden;
}

// The Bridge-call channel. @runic-artifex/views 0.20 or later reports each generated client call to it once a reader
// set `requested` and listens (specs/application, "Shared globals"), and then runs each call in a V8 async stack task,
// so a breakpoint after a reply shows the click that made the call. This client sends each settled call to the
// development server for the dock's call timeline (calls.ts), and draws it as an entry of the "Bridge calls" track in
// the "Runic" track group of Chromium's Performance panel (`performance.measure` with `detail.devtools`); other engines
// keep a plain measure or skip it. A .NET host in Development names each call's C# handler and its duration
// (handler-timing/1), which goes on the "C# handlers" track and in the dock's row. When the server exports traces
// (`trace` in the client options), the client also gives each call a W3C trace context, which its request carries to
// .NET (trace-context/1), and sends each settled call's span to the server.
const viewsCallsKey = Symbol.for("runic.views.calls");
const callsListenerKey = "runic:calls-listener";
const callsTraceKey = "runic:calls-trace";
const runicTrackGroup = "Runic";
const bridgeCallsTrack = "Bridge calls";
// .NET's own handler timing goes below the calls: an end event with `handler: { name, start, end }` (in
// `performance.now()` milliseconds) becomes an entry of this track.
const handlersTrack = "C# handlers";

interface CallTrace { readonly traceparent: string; readonly tracestate?: string }

interface ViewsCallsChannel {
  readonly listeners: Set<(event: CallEvent) => void>;
  requested?: boolean;
  devtools?: boolean;
  trace?: () => CallTrace | undefined;
}

interface CallEvent {
  readonly phase: string; readonly id: number; readonly kind: string; readonly request: string; readonly route: string;
  readonly contract: string; readonly member: string; readonly start: number; readonly end?: number; readonly duration?: number;
  readonly outcome?: string; readonly revision?: number; readonly reason?: string; readonly error?: unknown;
  readonly trace?: CallTrace;
  readonly handler?: { readonly name: string; readonly start: number; readonly end: number; readonly duration?: number };
}

type Crypto = { getRandomValues(array: Uint8Array): Uint8Array };

// A new root span for a call: a random trace id and span id, sampled, as W3C traceparent version 00.
function newTrace(): CallTrace | undefined {
  const crypto = (globalThis as { crypto?: Crypto }).crypto;
  if (typeof crypto?.getRandomValues !== "function") return undefined;
  const hex = (length: number) => Array.from(crypto.getRandomValues(new Uint8Array(length)), byte => byte.toString(16).padStart(2, "0")).join("");
  return { traceparent: `00-${hex(16)}-${hex(8)}-01` };
}

type UserTiming = { measure?(name: string, options: object): unknown; clearMeasures?(name?: string): void };

function viewsCalls(): ViewsCallsChannel {
  const channels = host as Record<symbol, ViewsCallsChannel | undefined>;
  return channels[viewsCallsKey] ??= { listeners: new Set() };
}

// A track entry. It is cleared from the page's timeline buffer at once: a recording already has it, and a long
// development session should not fill the buffer. An engine without User Timing level 3 skips it.
function measure(name: string, start: number, end: number, devtools: Record<string, unknown>): void {
  const timing = (globalThis as { performance?: UserTiming }).performance;
  if (typeof timing?.measure !== "function") return;
  try {
    timing.measure(name, { start, end, detail: { devtools } });
    timing.clearMeasures?.(name);
  } catch { /* Not supported. */ }
}

const outcomeColors: Readonly<Record<string, string>> = { ok: "primary", error: "error", cancelled: "tertiary" };

// The exception of a failed call: .NET's development detail of a `BridgeError`, else a browser error's own.
function callFailure(error: unknown): unknown {
  if (typeof error !== "object" || error === null) return undefined;
  const { name, message, stack, detail } = error as { name?: unknown; message?: unknown; stack?: unknown; detail?: unknown };
  if (typeof detail === "object" && detail !== null) {
    const { type, message: detailMessage, stack: detailStack } = detail as Record<string, unknown>;
    return { type: typeof type === "string" ? type : name, message: detailMessage, stack: detailStack };
  }
  return { type: name, message, stack };
}

// The dock's call timeline. The server names the handler from its source map.
function sendCall(event: CallEvent): void {
  const handler = typeof event.handler === "object" && event.handler !== null ? event.handler : undefined;
  const handlerDuration = typeof handler?.duration === "number" ? handler.duration
    : typeof handler?.start === "number" && typeof handler.end === "number" ? handler.end - handler.start : undefined;
  const report = sanitizeCallReport({
    page, kind: event.kind, route: event.route, contract: event.contract, member: event.member,
    duration: (event.end as number) - event.start, outcome: event.outcome, reason: event.reason, revision: event.revision,
    handlerDuration, handlerName: handler?.name,
    failure: event.outcome === "error" ? callFailure(event.error) : undefined,
  });
  if (report) send(callEvent, report);
}

// The span of a settled call with a trace context, for the server's OTLP export: epoch milliseconds, so the server
// needs no page clock.
function sendSpan(event: CallEvent, label: string): void {
  const trace = event.trace;
  if (options?.trace !== true || typeof trace?.traceparent !== "string" || typeof event.end !== "number") return;
  const origin = (globalThis as { performance?: { timeOrigin?: number } }).performance?.timeOrigin;
  if (typeof origin !== "number") return;
  const handler = event.handler;
  send(spanEvent, {
    traceparent: trace.traceparent, name: `${event.kind} ${label}`, start: origin + event.start, end: origin + event.end,
    kind: event.kind, request: event.request, route: event.route, contract: event.contract, member: event.member,
    outcome: event.outcome, reason: event.reason, revision: event.revision,
    ...typeof handler?.name === "string" && typeof handler.duration === "number" ? { handler: { name: handler.name, duration: handler.duration } } : {},
  });
}

function reportCall(event: CallEvent): void {
  if (typeof event !== "object" || event === null || event.phase !== "end") return;
  if (typeof event.start !== "number" || typeof event.end !== "number") return;
  sendCall(event);
  // `Demo.EditorViewModel:3F2A…` names the C# view model `EditorViewModel`.
  const model = String(event.contract).split(":", 1)[0]!.split(".").pop() || event.route;
  const label = `${model}.${event.member}`;
  sendSpan(event, label);
  if (viewsCalls().devtools === false) return;
  const outcome = String(event.outcome);
  const handler = event.handler;
  const timed = typeof handler === "object" && handler !== null && typeof handler.start === "number" && typeof handler.end === "number";
  const properties: [string, string][] = [
    ["Kind", String(event.request)], ["Route", String(event.route)], ["Contract", String(event.contract)],
    ["Request id", String(event.id)], ["Outcome", event.reason === undefined ? outcome : `${outcome} (${event.reason})`],
  ];
  if (typeof event.revision === "number") properties.push(["Revision", String(event.revision)]);
  if (timed) properties.push(["C# handler", `${String(handler.name)} (${(handler.end - handler.start).toFixed(2)} ms)`]);
  if (typeof event.trace?.traceparent === "string") properties.push(["traceparent", event.trace.traceparent]);
  measure(`${label} #${event.id}`, event.start, event.end, {
    dataType: "track-entry", track: bridgeCallsTrack, trackGroup: runicTrackGroup, color: outcomeColors[outcome] ?? "warning",
    properties, tooltipText: `${event.kind} ${label}: ${outcome}, ${(event.end - event.start).toFixed(1)} ms`,
  });
  if (timed) {
    measure(`${handler.name} #${event.id}`, handler.start, handler.end, {
      dataType: "track-entry", track: handlersTrack, trackGroup: runicTrackGroup, color: "secondary",
      properties: [["Request id", String(event.id)], ["Route", String(event.route)], ["Member", String(event.member)]],
    });
  }
}

function connectViewsCalls(): void {
  const calls = viewsCalls();
  const previous = data[callsListenerKey] as ((event: CallEvent) => void) | undefined;
  if (previous) calls.listeners.delete(previous);
  calls.listeners.add(reportCall);
  data[callsListenerKey] = reportCall;
  // Only while the server exports traces; otherwise no call gets a trace context.
  if (options?.trace === true) calls.trace = newTrace;
  else if (calls.trace === newTrace || data[callsTraceKey] === calls.trace) delete calls.trace;
  data[callsTraceKey] = calls.trace;
  calls.requested = true;
}

// Removes this copy's listeners once the HTTP channel has no server to report to.
function close(): void {
  channel = "closed";
  pending.length = 0;
  viewsDiagnostics().delete(reportViewsDiagnostic);
  viewsConnection().listeners.delete(reportConnection);
  viewsInspector().listeners.delete(reportInspector);
  const calls = viewsCalls();
  calls.listeners.delete(reportCall);
  if (calls.trace === newTrace) delete calls.trace;
  disposeInspect();
  host.removeEventListener?.("pagehide", pageHidden);
  if (stateTimer !== undefined) clearTimeout(stateTimer);
  stateTimer = undefined;
  pendingStates.clear();
}

// The Vite DevTools dock. Its own loader is an inline module that loads `/__devtools/embedded.js` relative to the
// document. Under `dotnet runic dev` the document is the .NET host's page, which loads Vite's modules from Vite's
// origin, so that URL names the host and the dock never appears; SvelteKit runs no `transformIndexHtml` and adds no
// loader at all. This client is a module of Vite's origin, so it loads the dock from there once the document's own
// scripts ran, unless one of them already loads that script.
const dockPath = "/__devtools/embedded.js";

function loadDock(): void {
  const document = (globalThis as { document?: Document }).document;
  if (options?.dock !== true || document === undefined) return;
  const source = new URL(dockPath, import.meta.url).href;
  const load = (): void => {
    for (const script of document.querySelectorAll("script[src]")) {
      if ((script as HTMLScriptElement).src === source) return;
    }
    const script = document.createElement("script");
    script.type = "module";
    script.src = source;
    document.body.appendChild(script);
  };
  // The document's module scripts, the dock's loader among them, all ran by the window's load event; this client runs
  // first, while the document is still "interactive", or later from a page module (SvelteKit's hooks.client.ts).
  if (document.readyState === "complete") load();
  else host.addEventListener?.("load", load);
}

connectViewsDiagnostics();
connectViewsConnection();
connectViewsInspector();
connectViewsCalls();
const inspectListenerKey = "runic:inspect";
let disposeInspect: () => void = () => {};
function connectInspect(): void {
  (data[inspectListenerKey] as (() => void) | undefined)?.();
  disposeInspect = options?.inspect === false ? () => {} : installInspect({ base: options?.base, root: options?.root });
  data[inspectListenerKey] = disposeInspect;
}
connectInspect();
// Vite's HMR context shows that a development server serves the page: install the warning now. Without it
// (a production build that kept the client), the HTTP channel installs it once the server answers. Installs once
// per page; HMR re-evaluation leaves it in place.
if (hot) installStaleInputWarning(globalThis);
loadDock();
