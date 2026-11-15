// The channel between the development client and the development server, shared by both sides.
//
// `runic()` loads the client through `virtual:runic/client`, which sets the client options below to the HMR channel:
// the client sends with `import.meta.hot.send`, and the plugin listens on `server.ws`. A client that loads otherwise
// (the Angular CLI's development server, which runs no Vite plugins, or an application import of the package) posts
// each message to `diagnosticsPath` instead, which `runic()` and `runicDevSessionProxy()` answer.
//
// A sanitized View state is at most about 16,000 characters (diagnostics.ts, `sanitizeViewState`).

export const diagnosticEvent = "runic:diagnostic";
export const connectionEvent = "runic:connection";
/** The state inspector: a View's state, a closed View, or a page that went away (`DevelopmentTimeline.applyView`). */
export const viewEvent = "runic:view";
/** A settled Bridge call for the dock's timeline (calls.ts, `RunicBridgeCallReport`). */
export const callEvent = "runic:call";
/** One settled Bridge call with a trace context, for the server's OTLP export (otlp.ts). */
export const spanEvent = "runic:span";

/**
 * A request of the development client over Vite's HMR connection, which reaches the development server from any page
 * that loaded Vite's modules, also the .NET host's page under `dotnet runic dev`, whose relative URLs name the host:
 * `{ id, kind: "sources" }` for the source map, `{ id, kind: "handlers", file }` for the handler analysis,
 * `{ id, kind: "message", target: { catalog, key } }` for a Translations message's sources, and
 * `{ id, kind: "open", target }` for `/__runic/open`'s `{ id }`, `{ file, line, column }` or `{ message }`. The server answers each on `responseEvent` with the same `id` (sources.ts,
 * `answerClientRequest`).
 */
export const requestEvent = "runic:request";
export const responseEvent = "runic:response";

export type RunicClientRequest =
  | { readonly id: number; readonly kind: "sources" }
  | { readonly id: number; readonly kind: "handlers"; readonly file: string }
  | { readonly id: number; readonly kind: "message"; readonly target: { readonly catalog: string; readonly key: string } }
  | { readonly id: number; readonly kind: "open"; readonly target: unknown };

/** The answer to a `RunicClientRequest`: an HTTP-like status, and for `sources` with 200 the map. */
export interface RunicClientResponse {
  readonly id: number;
  readonly status: number;
  readonly body?: unknown;
}

/** The endpoint a client posts `{ "event", "data" }` to, one message per request, answered with 204. */
export const diagnosticsPath = "/__runic/diagnostics";

/**
 * The largest request body the endpoint accepts. A sanitized diagnostic with a failure is at most about 12,000
 * characters, so this leaves room for JSON escaping and multibyte text.
 */
export const maximumClientMessageBytes = 65_536;

/** The development source map and the Runic open-in-editor action (sources.ts), which Runic Inspect reads and calls. */
export const sourcesPath = "/__runic/sources";
export const openPath = "/__runic/open";
/** Runic Inspect's handler analysis of a frontend file (handlers.ts): which event handlers reach which client calls. */
export const handlersPath = "/__runic/handlers";

/** `{ channel?: "hot", … }` on `globalThis`, set by `virtual:runic/client` before the client evaluates. */
export const clientOptionsKey = Symbol.for("runic.vite.client");

export interface RunicClientOptions {
  readonly channel?: "hot";
  /** The Vite DevTools dock is registered: the client loads it when the document does not (client.ts, `loadDock`). */
  readonly dock?: boolean;
  /** `false` leaves Runic Inspect out (`runic({ inspect: false })`). */
  readonly inspect?: boolean;
  /** The server's base path. */
  readonly base?: string;
  /** The Vite root, which relative component paths (Svelte, Angular) are relative to. */
  readonly root?: string;
  /**
   * The development server exports the page's spans of Bridge calls over OTLP: the client gives each call a W3C trace
   * context (which its request carries to .NET) and sends the call's span as a `runic:span` message.
   */
  readonly trace?: boolean;
}
