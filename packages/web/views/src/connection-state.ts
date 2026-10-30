import { hostCallbacks, reportBridgeError, type RunicBridgeClient } from "./transport.js";

/**
 * The connection between this page and its .NET host.
 *
 * - `connecting`: the Bridge has not connected yet.
 * - `connected`: the Bridge is connected.
 * - `reconnecting`: the connection dropped and the host transport is trying to restore it.
 * - `lost`: the Bridge has not been connected for `lostAfter` milliseconds, or never connected
 *   in that time. `lost` is not terminal: the state returns to `connected` when the host comes back.
 * - `closed`: the application closed this window (`presented/1`, a producer of 0.15 or later). The
 *   state stays `closed` when the connection then drops, instead of `reconnecting` or `lost`, and
 *   returns to `connected` when a new connection opens.
 */
export type BridgeConnectionState = "connecting" | "connected" | "reconnecting" | "lost" | "closed";

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
  // What the host last reported, for when the window's `closed` state ends.
  let connectedNow = false;
  const subscriptions: (() => void)[] = [];
  const channel = connectionChannel();

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
    connectedNow = connected;
    if (channel.closed === true) {
      // The application closed the window: a drop that follows is the end of the window, not a lost connection.
      clearTimeout(lostTimer);
      lostTimer = undefined;
      publish("closed");
      return;
    }
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
    if (typeof bridge?.onConnectionChange === "function" || everConnected) return;
    if (bridge?.isConnected()) connectedChanged(true);
    else pollTimer = setTimeout(poll, pollInterval);
  }
  function attach(installed: RunicBridgeClient): void {
    bridge = installed;
    // `onConnectionChange` delivers the current state at once.
    // A host script of protocol 3 or earlier may lack it; the poll then reports the first connection.
    if (typeof installed.onConnectionChange === "function") subscriptions.push(installed.onConnectionChange(connectedChanged));
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
  // A closed window ends with a new connection; the host reports that connection too, so wait for it otherwise.
  const closedChanged = (closed: boolean) => { if (closed || connectedNow) connectedChanged(connectedNow); };
  (channel.closedListeners ??= new Set()).add(closedChanged);

  connectedChanged(false);
  poll();
  pageShow();
  return () => {
    if (stopped) return;
    stopped = true;
    clearTimeout(lostTimer);
    clearTimeout(pollTimer);
    for (const unsubscribe of subscriptions) unsubscribe();
    channel.closedListeners?.delete(closedChanged);
    target.removeEventListener?.("pagehide", pageHide);
    target.removeEventListener?.("pageshow", pageShow);
  };
}

// The page's connection state on a registered channel, which copies of this package and
// `@runic-artifex/vite-plugin-runic/client` share without a dependency (specs/application,
// "Shared globals"). The first View that connects starts it, or, from page load, the Vite
// development client: it sets `requested` and calls `start`. It then follows the host for the page.
const connectionChannelKey = Symbol.for("runic.views.connection");

interface ConnectionChannel {
  readonly listeners: Set<(state: BridgeConnectionState) => void>;
  state?: BridgeConnectionState;
  started?: boolean;
  requested?: boolean;
  start?: () => void;
  /**
   * Whether the application closed the window (presented/1): from the `closing` push until a new connection. The page
   * runtime sets it; every `onBridgeConnection` of every copy of this package follows it through `closedListeners`.
   */
  closed?: boolean;
  closedListeners?: Set<(closed: boolean) => void>;
}

function connectionChannel(): ConnectionChannel {
  const host = globalThis as unknown as Record<symbol, ConnectionChannel | undefined>;
  return host[connectionChannelKey] ??= { listeners: new Set() };
}

/** @internal The page runtime reports that the application closed the window, and that a new connection opened. */
export function reportWindowClosed(closed: boolean): void {
  const channel = connectionChannel();
  if ((channel.closed === true) === closed) return;
  channel.closed = closed;
  for (const listener of [...channel.closedListeners ?? []]) {
    try { listener(closed); } catch (error) { reportBridgeError(error); }
  }
}

export function publishConnectionState(): void {
  const channel = connectionChannel();
  if (channel.started) return;
  channel.started = true;
  onBridgeConnection(state => {
    channel.state = state;
    for (const listener of [...channel.listeners]) {
      try { listener(state); } catch (error) { reportBridgeError(error); }
    }
  });
}

// A development client that loaded first has set `requested`; one that loads later calls `start`.
{
  const channel = connectionChannel();
  channel.start ??= publishConnectionState;
  if (channel.requested) publishConnectionState();
}
