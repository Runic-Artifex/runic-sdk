import { BridgeError } from "./errors.js";

/** The host-neutral Bridge a host script installs as `window.__runicBridge`. */
export interface RunicBridgeClient {
  /** The host connection is authenticated. */
  isConnected(): boolean;
  /** Invokes a .NET route. Rejects when the transport fails. */
  call(name: string, ...args: unknown[]): Promise<string>;
  /** Calls `listener` after the connection is re-established without a page reload. */
  onReconnect?(listener: () => void): () => void;
}

type BridgeHost = typeof globalThis & { __runicBridge?: RunicBridgeClient };

/** Waits for the installed host Bridge to report a connection to .NET. */
export async function waitForBridge(timeoutMilliseconds = 5_000): Promise<RunicBridgeClient> {
  const host = globalThis as BridgeHost;
  const deadline = Date.now() + timeoutMilliseconds;
  while (!host.__runicBridge?.isConnected()) {
    if (Date.now() >= deadline) throw new BridgeError("timeout", "The Bridge did not connect to .NET in time.");
    await new Promise<void>(resolve => setTimeout(resolve, 50));
  }
  return host.__runicBridge;
}

/** Window members .NET calls (`__{route}Changed`) and the shared runtime slot. */
export function hostCallbacks(): Record<string | symbol, unknown> {
  return globalThis as unknown as Record<string | symbol, unknown>;
}

// A throwing subscriber or an undecodable push must not stop delivery to
// other subscribers or unwind into the .NET callback that pushed state.
export function reportBridgeError(error: unknown): void {
  const report = (globalThis as { reportError?: (error: unknown) => void }).reportError;
  if (typeof report === "function") report(error); else console.error(error);
}
