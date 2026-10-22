import { assertInInjectionContext, DestroyRef, inject, Injector, PLATFORM_ID, signal, type Signal } from "@angular/core";
import { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. */
  readonly state: Signal<BridgeConnectionState>;
}

export interface InjectBridgeConnectionOptions extends BridgeConnectionOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Follows the page's connection to its .NET host (`connecting`, `connected`, `reconnecting` or
 * `lost`) as a signal through `onBridgeConnection`, for the lifetime of the injection context.
 * Destruction unsubscribes. Under server rendering (`PLATFORM_ID` `"server"`) it does not
 * observe and stays `connecting`.
 *
 * ```ts
 * readonly connection = injectBridgeConnection();
 * // @if (connection.state() === "lost") { <p role="alert">The application stopped responding.</p> }
 * ```
 */
export function injectBridgeConnection(
  { injector: provided, ...options }: InjectBridgeConnectionOptions = {},
): BridgeConnectionHandle {
  if (!provided) assertInInjectionContext(injectBridgeConnection);
  const injector = provided ?? inject(Injector);
  const state = signal<BridgeConnectionState>("connecting");
  if (injector.get(PLATFORM_ID, null) !== "server") {
    const stop = onBridgeConnection(next => state.set(next), options);
    injector.get(DestroyRef).onDestroy(stop);
  }
  return { state: state.asReadonly() };
}
