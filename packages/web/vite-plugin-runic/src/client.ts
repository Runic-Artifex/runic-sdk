// The development client that the plugin injects into served pages. It forwards the
// diagnostics and connection state of `@runic-artifex/views` to the DevTools dock. It has
// no exports: applications do not import it.
import { sanitizeDiagnosticSummary } from "./diagnostics.js";

interface ViteHotContextLike {
  readonly data: Record<string, unknown>;
  send(event: string, data?: unknown): void;
}

interface ImportMetaWithHot extends ImportMeta {
  readonly hot?: ViteHotContextLike;
}

const hot = (import.meta as ImportMetaWithHot).hot;
const diagnosticEvent = "runic:diagnostic";
const connectionEvent = "runic:connection";

function sendDiagnostic(candidate: unknown): void {
  const summary = sanitizeDiagnosticSummary(candidate);
  if (summary) hot?.send(diagnosticEvent, summary);
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
  return "BridgeError";
}

/** Forwards a Views runtime diagnostic to the DevTools timeline. */
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

function connectViewsDiagnostics(): void {
  if (!hot) return;
  const host = globalThis as unknown as Record<symbol, Set<(diagnostic: ViewsDiagnostic) => void> | undefined>;
  const listeners = host[viewsDiagnosticsKey] ??= new Set();
  // HMR re-evaluates this module; replace the listener of the previous copy.
  const previous = hot.data[viewsListenerKey] as ((diagnostic: ViewsDiagnostic) => void) | undefined;
  if (previous) listeners.delete(previous);
  listeners.add(reportViewsDiagnostic);
  hot.data[viewsListenerKey] = reportViewsDiagnostic;
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
  hot?.send(connectionEvent, { state });
}

function connectViewsConnection(): void {
  if (!hot) return;
  const host = globalThis as unknown as Record<symbol, ViewsConnectionChannel | undefined>;
  const channel = host[viewsConnectionKey] ??= { listeners: new Set() };
  const previous = hot.data[connectionListenerKey] as ((state: string) => void) | undefined;
  if (previous) channel.listeners.delete(previous);
  channel.listeners.add(reportConnection);
  hot.data[connectionListenerKey] = reportConnection;
  if (typeof channel.state === "string") reportConnection(channel.state);
  // Observe from page load, not only once a View connects: a copy of @runic-artifex/views that
  // loads later sees `requested`, and one that already loaded has set `start`.
  channel.requested = true;
  if (typeof channel.start === "function") channel.start();
}

connectViewsDiagnostics();
connectViewsConnection();
