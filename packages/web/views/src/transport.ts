import { emitBridgeDiagnostic, emitErrorDiagnostic, suppressErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import { abortable, throwIfAborted } from "./lifetime.js";
import { bridgeProtocol } from "./version.js";

/**
 * The host bridge a host script installs as `globalThis.__runicBridge` (Views protocol 4, specs/application,
 * "Host bridge"). Messages cross it as JSON text; the Views runtime parses each one once.
 */
export interface RunicBridgeClient {
  /** Whether the host connection is open and authenticated. */
  isConnected(): boolean;
  /**
   * Sends one request as JSON text and resolves with the reply as JSON text. Rejects when the transport fails
   * before a reply arrives.
   */
  request(message: string): Promise<string>;
  /**
   * Calls `listener` with the JSON text of each push, in order, until the returned function is called. Pushes
   * received while no listener exists are dropped. The page's Views runtime is the only listener.
   */
  onMessage(listener: (message: string) => void): () => void;
  /**
   * Calls `listener(connected)` at once and after each change, until the returned function is called. A host reports
   * `false` before the `true` of a new connection, so each `true` after `false` is a new connection, which needs its
   * own `hello`. It has none of the side effects that `isConnected()` may have.
   */
  onConnectionChange(listener: (connected: boolean) => void): () => void;
}

export interface WaitForBridgeOptions {
  /** Milliseconds to wait for the host Bridge to connect. Defaults to 5000. */
  readonly timeout?: number;
  /**
   * Stops waiting when it aborts: the wait rejects with `signal.reason`, an
   * `AbortError` DOMException unless `abort(reason)` gave another one. An
   * already aborted signal rejects at once. The wait removes its abort listener
   * when it settles.
   */
  readonly signal?: AbortSignal | undefined;
}

type BridgeHost = typeof globalThis & { __runicBridge?: RunicBridgeClient };

const defaultTimeout = 5_000;
const unavailableReported = Symbol.for("runic.views.unavailableReported");

// The message of an `unavailable` BridgeError, with the usual remediation.
const missingBridgeMessage =
  "No Runic Bridge is installed: window.__runicBridge is undefined. Open this page through its .NET host " +
  "(`dotnet runic dev` or the running application) instead of a plain Vite server, or install a mock with " +
  "installMockBridge() from @runic-artifex/views/mock before the first connect call.";

/**
 * Waits for the installed host Bridge to report a connection to .NET.
 * Rejects with an `unavailable` BridgeError when no host installed
 * `window.__runicBridge`, with a `timeout` BridgeError when it did not connect in time,
 * with a `failed` BridgeError (and a `protocol` diagnostic) when the host implements Views protocol 3 or
 * earlier, and with the abort reason when `options.signal` aborts first.
 */
export async function waitForBridge(options: WaitForBridgeOptions = {}): Promise<RunicBridgeClient> {
  const timeout = options.timeout ?? defaultTimeout;
  if (!Number.isFinite(timeout) || timeout < 0) throw new RangeError("The Bridge timeout must be a non-negative number of milliseconds.");
  const { signal } = options;
  throwIfAborted(signal);
  const host = globalThis as BridgeHost;
  const deadline = Date.now() + timeout;
  while (!host.__runicBridge?.isConnected()) {
    if (Date.now() >= deadline) {
      const error = host.__runicBridge === undefined
        ? new BridgeError("unavailable", missingBridgeMessage)
        : new BridgeError("timeout", `The Runic Bridge is installed but did not connect to .NET within ${timeout} ms. ` +
          "Check that the .NET host is running and that its output shows no startup error.");
      // Every connect call fails the same way on a page without a Bridge;
      // DevTools gets the missing Bridge once per page.
      const page = globalThis as unknown as Record<symbol, unknown>;
      if (error.kind !== "unavailable" || page[unavailableReported] !== true) emitErrorDiagnostic(error);
      if (error.kind === "unavailable") page[unavailableReported] = true;
      throw error;
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    try { await abortable(new Promise<void>(resolve => { timer = setTimeout(resolve, 50); }), signal); }
    finally { clearTimeout(timer); }
  }
  const bridge = host.__runicBridge;
  assertMessageBridge(bridge);
  return bridge;
}

const protocolReported = Symbol.for("runic.views.protocolReported");

/**
 * Reports a host of another protocol: one `protocol` diagnostic per page, and every error that reports it
 * is marked as reported, so it is the only report.
 */
export function reportProtocolMismatch(error: BridgeError): void {
  const page = globalThis as unknown as Record<symbol, unknown>;
  if (page[protocolReported] === true) { suppressErrorDiagnostic(error); return; }
  page[protocolReported] = true;
  emitBridgeDiagnostic({ kind: "error", code: "protocol", message: error.message, error, ...(error.route === undefined ? {} : { route: error.route }) });
}

/** The error of a host that speaks another protocol. `host` describes it, such as "uses Views protocol 5". */
export function protocolMismatchError(host: string): BridgeError {
  return new BridgeError("failed", `The .NET host ${host}, but this client implements protocol ${bridgeProtocol}, so the View ` +
    "cannot connect. Use Runic.Application.Views and @runic-artifex/views of one release, rebuild so the generated clients " +
    "are regenerated, and reload the page.", { reason: "protocol-mismatch" });
}

// A host script of protocol 3 or earlier installs `call(name, …args)` instead of `request(message)`.
function assertMessageBridge(bridge: RunicBridgeClient): void {
  if (typeof (bridge as Partial<RunicBridgeClient>).request === "function") return;
  const error = protocolMismatchError("implements Views protocol 3 or earlier (its host bridge has no `request`)");
  reportProtocolMismatch(error);
  throw error;
}

/** Window members and the shared runtime slot. */
export function hostCallbacks(): Record<string | symbol, unknown> {
  return globalThis as unknown as Record<string | symbol, unknown>;
}

// A View the page or .NET released, as `released` while the Bridge is connected
// and as `disconnected` when it is not.
export function notConnectedError(bridge: RunicBridgeClient, message: string, route?: string): BridgeError {
  return bridge.isConnected()
    ? new BridgeError("released", message, route === undefined ? {} : { route })
    : new BridgeError("disconnected", "The Bridge is disconnected.", route === undefined ? {} : { route });
}

// A throwing subscriber or an undecodable push must not stop delivery to
// other subscribers or unwind into the host script that pushed it.
// A released View is expected after navigation and is not reported.
export function reportBridgeError(error: unknown, route?: string): void {
  if (error instanceof BridgeError && error.kind === "released") return;
  emitErrorDiagnostic(error, route);
  const report = (globalThis as { reportError?: (error: unknown) => void }).reportError;
  if (typeof report === "function") report(error); else console.error(error);
}
