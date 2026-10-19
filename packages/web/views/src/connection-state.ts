import { hostCallbacks, reportBridgeError, type RunicBridgeClient } from "./transport.js";

/**
 * The connection between this page and its .NET host.
 *
 * - `connecting`: the Bridge has not connected yet.
 * - `connected`: the Bridge is connected.
 * - `reconnecting`: the connection dropped and the host transport is trying to restore it.
 * - `lost`: the Bridge has not been connected for `lostAfter` milliseconds, or never connected
 *   in that time. `lost` is not terminal: the state returns to `connected` when the host comes back.
 */
export type BridgeConnectionState = "connecting" | "connected" | "reconnecting" | "lost";

export interface BridgeConnectionOptions {
  /**
   * Milliseconds without a connection, after a drop or from the start, before the state is
   * `lost`. Defaults to 5000. `0` reports `lost` as soon as the connection drops.
   */
  readonly lostAfter?: number;
}

type BridgeHost = { __runicBridge?: RunicBridgeClient };

const defaultLostAfter = 5_000;
// While no Bridge is installed, or a host without `onConnectionChange` has not connected yet.
const pollInterval = 50;

/**
 * Observes the connection to the .NET host. Calls `listener` with the current state at once,
 * then after each change, until the returned function is called.
 *
 * A host script reports a dropped transport as it happens (Runic Desktop and CS-WebUI within
 * about 250 ms of the WebSocket closing), so the page learns that the host ended without making
 * a call. A host script without `onConnectionChange` reports only the first connection and each
 * reconnect. The page stops reporting while it unloads, so a reload or navigation does not show
 * `reconnecting`.
 */
export function onBridgeConnection(listener: (state: BridgeConnectionState) => void,
  options: BridgeConnectionOptions = {}): () => void {
  const lostAfter = options.lostAfter ?? defaultLostAfter;
  if (!Number.isFinite(lostAfter) || lostAfter < 0) throw new RangeError("lostAfter must be a non-negative number of milliseconds.");
  const host = hostCallbacks() as BridgeHost;
  let state: BridgeConnectionState | undefined;
  let stopped = false;
  // The listener gets the state once the subscription has read it, not each step of reading it.
  let hidden = true;
  let everConnected = false;
  let lostTimer: ReturnType<typeof setTimeout> | undefined;
  let pollTimer: ReturnType<typeof setTimeout> | undefined;
  let bridge: RunicBridgeClient | undefined;
  const subscriptions: (() => void)[] = [];

  function publish(next: BridgeConnectionState): void {
    if (stopped || next === state) return;
    state = next;
    if (hidden) return;
    try { listener(next); } catch (error) { reportBridgeError(error); }
  }
  function startLostTimer(): void {
    if (lostTimer !== undefined) return;
    lostTimer = setTimeout(() => { lostTimer = undefined; publish("lost"); }, lostAfter);
  }
  function connectedChanged(connected: boolean): void {
    if (stopped) return;
    if (connected) {
      clearTimeout(lostTimer);
      lostTimer = undefined;
      everConnected = true;
      publish("connected");
      return;
    }
    if (state === "lost") return;
    if (lostAfter === 0) { publish("lost"); return; }
    publish(everConnected ? "reconnecting" : "connecting");
    startLostTimer();
  }
  // Waits for a host script to install the Bridge. Without `onConnectionChange`, also
  // polls an installed Bridge until it first connects; never after that, because a host's
  // `isConnected()` can have side effects (CS-WebUI allows navigation).
  function poll(): void {
    pollTimer = undefined;
    if (stopped) return;
    if (bridge === undefined && host.__runicBridge !== undefined) attach(host.__runicBridge);
    if (bridge?.onConnectionChange !== undefined || everConnected) return;
    if (bridge?.isConnected()) connectedChanged(true);
    else pollTimer = setTimeout(poll, pollInterval);
  }
  function attach(installed: RunicBridgeClient): void {
    bridge = installed;
    // `onConnectionChange` delivers the current state at once.
    if (installed.onConnectionChange) { subscriptions.push(installed.onConnectionChange(connectedChanged)); return; }
    // Without it a loss goes unseen, but a reconnect still means connected.
    if (installed.onReconnect) subscriptions.push(installed.onReconnect(() => connectedChanged(true)));
  }
  const target = globalThis as { addEventListener?: typeof addEventListener; removeEventListener?: typeof removeEventListener };
  const pageHide = () => { hidden = true; };
  // A page restored from the back/forward cache reports its current state again.
  const pageShow = () => {
    if (!hidden) return;
    hidden = false;
    const current = state;
    state = undefined;
    if (current !== undefined) publish(current);
  };
  target.addEventListener?.("pagehide", pageHide);
  target.addEventListener?.("pageshow", pageShow);

  connectedChanged(false);
  poll();
  pageShow();
  return () => {
    if (stopped) return;
    stopped = true;
    clearTimeout(lostTimer);
    clearTimeout(pollTimer);
    for (const unsubscribe of subscriptions) unsubscribe();
    target.removeEventListener?.("pagehide", pageHide);
    target.removeEventListener?.("pageshow", pageShow);
  };
}
