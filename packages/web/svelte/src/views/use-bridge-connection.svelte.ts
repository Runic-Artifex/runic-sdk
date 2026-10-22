import { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. A reactive property. */
  readonly state: BridgeConnectionState;
}

/**
 * Follows the page's connection to its .NET host (`connecting`, `connected`, `reconnecting` or
 * `lost`) through `onBridgeConnection` while its owner lives. `state` holds the current state at
 * once; the subscription starts with the owner's effects, so server rendering never observes.
 * Call during component initialization (or in `$effect.root`); owner destruction unsubscribes.
 *
 * ```svelte
 * const connection = useBridgeConnection();
 * {#if connection.state === "lost"}<p role="alert">The application stopped responding.</p>{/if}
 * ```
 */
export function useBridgeConnection(options: BridgeConnectionOptions = {}): BridgeConnectionHandle {
  // The subscription reports the current state at once; reading it validates `lostAfter` too.
  let initial: BridgeConnectionState = "connecting";
  onBridgeConnection(next => { initial = next; }, options)();
  let state = $state<BridgeConnectionState>(initial);
  $effect.pre(() => onBridgeConnection(next => { state = next; }, options));
  return { get state() { return state; } };
}
