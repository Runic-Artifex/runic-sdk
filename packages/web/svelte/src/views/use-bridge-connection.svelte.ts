import { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";
import { getRunicInstance } from "./instance.js";

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. A reactive property. */
  readonly state: BridgeConnectionState;
}

/**
 * Follows the connection of the component's Runic instance (the page's, by default) to its .NET host (`connecting`, `connected`, `reconnecting`,
 * `lost` or `closed`) through `onBridgeConnection` while its owner lives. `state` holds the current state at
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
  const instance = getRunicInstance();
  let initial: BridgeConnectionState = "connecting";
  instance.run(() => onBridgeConnection(next => { initial = next; }, options))();
  let state = $state<BridgeConnectionState>(initial);
  $effect.pre(() => instance.run(() => onBridgeConnection(next => { state = next; }, options)));
  return { get state() { return state; } };
}
