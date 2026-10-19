import { emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";

/** The host-neutral Bridge a host script installs as `window.__runicBridge`. */
export interface RunicBridgeClient {
  /** The host connection is authenticated. */
  isConnected(): boolean;
  /** Invokes a .NET route. Rejects when the transport fails. */
  call(name: string, ...args: unknown[]): Promise<string>;
  /** Calls `listener` after the connection is re-established without a page reload. */
  onReconnect?(listener: () => void): () => void;
  /**
   * Calls `listener` with whether the host connection is authenticated, at once and then after
   * each change, including a dropped transport, until the returned function is called.
   */
  onConnectionChange?(listener: (connected: boolean) => void): () => void;
}

export interface WaitForBridgeOptions {
  /** Milliseconds to wait for the host Bridge to connect. Defaults to 5000. */
  readonly timeout?: number;
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
 * `window.__runicBridge`, and with a `timeout` BridgeError when it did not connect in time.
 */
export function waitForBridge(timeoutMilliseconds?: number): Promise<RunicBridgeClient>;
export function waitForBridge(options?: WaitForBridgeOptions): Promise<RunicBridgeClient>;
export async function waitForBridge(options: number | WaitForBridgeOptions = {}): Promise<RunicBridgeClient> {
  const timeout = typeof options === "number" ? options : options.timeout ?? defaultTimeout;
  if (!Number.isFinite(timeout) || timeout < 0) throw new RangeError("The Bridge timeout must be a non-negative number of milliseconds.");
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
    await new Promise<void>(resolve => setTimeout(resolve, 50));
  }
  return host.__runicBridge;
}

/** Window members .NET calls (`__{route}Changed`) and the shared runtime slot. */
export function hostCallbacks(): Record<string | symbol, unknown> {
  return globalThis as unknown as Record<string | symbol, unknown>;
}

// A host answers a late call of a route it released, such as a route of a View
// that .NET released during the call, with a `disconnected` envelope. Runic
// Desktop and CS-WebUI send the same one. The host answered, so the Bridge is
// connected and only the View was released. An empty reply (a Runic Desktop
// handler that threw, or a capability that was never admitted) stays an invalid response.
export function releasedRouteError(route: string): BridgeError {
  return new BridgeError("released", "This View was released: its route is no longer bound.", { route });
}

// A View the page or .NET released, as `released` while the Bridge is connected
// and as `disconnected` when it is not.
export function notConnectedError(bridge: RunicBridgeClient, message: string, route?: string): BridgeError {
  return bridge.isConnected()
    ? new BridgeError("released", message, route === undefined ? {} : { route })
    : new BridgeError("disconnected", "The Bridge is disconnected.", route === undefined ? {} : { route });
}

// A throwing subscriber or an undecodable push must not stop delivery to
// other subscribers or unwind into the .NET callback that pushed state.
// A released View is expected after navigation and is not reported.
export function reportBridgeError(error: unknown, route?: string): void {
  if (error instanceof BridgeError && error.kind === "released") return;
  emitErrorDiagnostic(error, route);
  const report = (globalThis as { reportError?: (error: unknown) => void }).reportError;
  if (typeof report === "function") report(error); else console.error(error);
}
