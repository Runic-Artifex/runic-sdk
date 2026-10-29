// The channel between the development client and the development server, shared by both sides.
//
// `runic()` loads the client through `virtual:runic/client`, which sets the client options below to the HMR channel:
// the client sends with `import.meta.hot.send`, and the plugin listens on `server.ws`. A client that loads otherwise
// (the Angular CLI's development server, which runs no Vite plugins, or an application import of the package) posts
// each message to `diagnosticsPath` instead, which `runic()` and `runicDevSessionProxy()` answer.

export const diagnosticEvent = "runic:diagnostic";
export const connectionEvent = "runic:connection";

/** The endpoint a client posts `{ "event", "data" }` to, one message per request, answered with 204. */
export const diagnosticsPath = "/__runic/diagnostics";

/**
 * The largest request body the endpoint accepts. A sanitized diagnostic with a failure is at most about 12,000
 * characters, so this leaves room for JSON escaping and multibyte text.
 */
export const maximumClientMessageBytes = 65_536;

/** `{ channel?: "hot" }` on `globalThis`, set by `virtual:runic/client` before the client evaluates. */
export const clientOptionsKey = Symbol.for("runic.vite.client");

export interface RunicClientOptions {
  readonly channel?: "hot";
}
