// The development client. It forwards the diagnostics and connection state of `@runic-artifex/views` to the
// development server, which keeps them on its timeline, reports each View's state and superseded command replies to
// the state inspector, and warns about script that overwrites a focused text field with an older value
// (`stale-input`). It has no exports.
//
// `runic()` injects it into served pages through `virtual:runic/client`, and loads it through that module for an
// application import of `@runic-artifex/vite-plugin-runic/client`; the build replaces it with an empty module. A
// development server without `runic()`, such as the Angular CLI's, serves the package itself: the page imports it
// in development only, and it reports to `runicDevSessionProxy()` (see channel.ts).
import type { RunicClientOptions } from "./channel.js";
import {
  clientOptionsKey,
  connectionEvent,
  diagnosticEvent,
  diagnosticsPath,
  maximumClientMessageBytes,
  viewEvent,
} from "./channel.js";
import { sanitizeDiagnosticSummary, sanitizeViewState } from "./diagnostics.js";
import { installStaleInputWarning } from "./stale-input.js";

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

// Removes this copy's listeners once the HTTP channel has no server to report to.
function close(): void {
  channel = "closed";
  pending.length = 0;
  viewsDiagnostics().delete(reportViewsDiagnostic);
  viewsConnection().listeners.delete(reportConnection);
  viewsInspector().listeners.delete(reportInspector);
  host.removeEventListener?.("pagehide", pageHidden);
  if (stateTimer !== undefined) clearTimeout(stateTimer);
  stateTimer = undefined;
  pendingStates.clear();
}

connectViewsDiagnostics();
connectViewsConnection();
connectViewsInspector();
// Vite's HMR context shows that a development server serves the page: install the warning now. Without it
// (a production build that kept the client), the HTTP channel installs it once the server answers. Installs once
// per page; HMR re-evaluation leaves it in place.
if (hot) installStaleInputWarning(globalThis);
