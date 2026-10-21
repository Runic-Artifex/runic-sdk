import { useCallback, useEffect, useReducer, useSyncExternalStore } from "react";
import type { ViewClient, ViewConnector } from "@runic-artifex/views";
import { isViewClient } from "@runic-artifex/views/bindings";

type SuspendedConnection = {
  status: "pending" | "ready" | "failed";
  promise: Promise<void>;
  client?: ViewClient;
  error?: unknown;
  users: number;
  /** The largest `releaseTimeout` of the components that rendered this entry. */
  releaseTimeout: number;
  timer?: ReturnType<typeof setTimeout> | undefined;
};

// Suspense discards the state of a component that never mounted, so pending
// connections live here, keyed by connect function, until a component claims them.
const suspended = new WeakMap<object, SuspendedConnection>();
const defaultReleaseTimeout = 10_000;
// setTimeout runs a larger delay, like Infinity, at once.
const maxReleaseTimeout = 2_147_483_647;

function checkReleaseTimeout(value: number | undefined): number {
  if (value === undefined) return defaultReleaseTimeout;
  if (value === Infinity || (Number.isInteger(value) && value >= 1 && value <= maxReleaseTimeout)) return value;
  throw new RangeError(`useSuspenseView's releaseTimeout must be a whole number of milliseconds from 1 to ${maxReleaseTimeout}, ` +
    `or Infinity, not ${String(value)}.`);
}

// Releases an entry no mounted component uses after `delay`: disposes its client, or
// forgets its failure so that a later render connects again. Infinity keeps it.
function releaseLater(key: object, entry: SuspendedConnection, delay: number): void {
  clearTimeout(entry.timer);
  entry.timer = delay === Infinity ? undefined : setTimeout(() => {
    if (entry.users !== 0 || suspended.get(key) !== entry) return;
    suspended.delete(key);
    entry.client?.dispose();
  }, delay);
}

function suspendedConnection(connector: ViewConnector<ViewClient>, releaseTimeout: number): SuspendedConnection {
  const key = connector.connect;
  const existing = suspended.get(key);
  if (existing) {
    if (releaseTimeout > existing.releaseTimeout) {
      existing.releaseTimeout = releaseTimeout;
      // A settled entry that nobody uses waits for the longer timeout from now.
      if (existing.status !== "pending" && existing.users === 0) releaseLater(key, existing, releaseTimeout);
    }
    return existing;
  }
  const entry: SuspendedConnection = { status: "pending", promise: Promise.resolve(), users: 0, releaseTimeout };
  entry.promise = Promise.resolve().then(() => connector.connect()).then(client => {
    entry.status = "ready";
    entry.client = client;
    // Release a connection whose render was abandoned before it mounted.
    releaseLater(key, entry, entry.releaseTimeout);
  }, (error: unknown) => {
    entry.status = "failed";
    entry.error = error;
    // Keep the failure for the error boundary; forget it later so a later render reconnects.
    releaseLater(key, entry, entry.releaseTimeout);
  });
  suspended.set(key, entry);
  return entry;
}

/**
 * Forgets a failed `useSuspenseView` connection, so the next render connects
 * again. Call it from an error boundary's reset before rendering the
 * component again. A connection that is pending or connected is unaffected.
 */
export function retrySuspenseView(source: ViewConnector<ViewClient>): void {
  const entry = suspended.get(source.connect);
  if (entry?.status !== "failed") return;
  clearTimeout(entry.timer);
  suspended.delete(source.connect);
}

export interface SuspenseViewOptions {
  /**
   * Milliseconds that a connection no mounted component has claimed, or a failure no
   * `retrySuspenseView` has cleared, stays cached: a whole number from 1 to 2147483647,
   * or `Infinity` to keep it until it is claimed or retried. Defaults to 10000. This is
   * not a connection timeout. Components that share a `connect` function share one
   * entry, which keeps the largest value any of them passed. Other values throw a
   * `RangeError`.
   */
  readonly releaseTimeout?: number;
}

export interface SuspenseViewHandle<TClient extends ViewClient> {
  /** The latest state. */
  readonly state: TClient["snapshot"];
  /** The connected client. */
  readonly client: TClient;
}

/**
 * Suspends the component until a generated View reference connects, then
 * renders its state. A failed connection is thrown to the nearest error
 * boundary; call `retrySuspenseView(reference)` when resetting the boundary to
 * connect again. An unretried failure, and a connection whose component never
 * mounted, is released after `options.releaseTimeout` (ten seconds by default).
 *
 * Components that pass a reference with the same `connect` function share one
 * client, which is disposed after the last of them unmounts. Pass a stable
 * function such as a generated page reference or `{ connect: connectWorkspace }`:
 * a function recreated on every render never resolves. An already connected
 * client is observed and left to its owner.
 */
export function useSuspenseView<TClient extends ViewClient>(
  source: ViewConnector<TClient> | TClient,
  options: SuspenseViewOptions = {},
): SuspenseViewHandle<TClient> {
  const releaseTimeout = checkReleaseTimeout(options.releaseTimeout);
  const connector = isViewClient(source) ? undefined : source;
  const entry = connector ? suspendedConnection(connector, releaseTimeout) : undefined;
  const [, refresh] = useReducer((count: number) => count + 1, 0);
  useEffect(() => {
    if (!connector || !entry) return;
    const key = connector.connect;
    // The connection was released before this component committed: connect again.
    if (suspended.get(key) !== entry) { refresh(); return; }
    entry.users++;
    clearTimeout(entry.timer);
    return () => {
      entry.users--;
      if (entry.users === 0) releaseLater(key, entry, 0);
    };
  }, [entry]);
  if (entry?.status === "pending") throw entry.promise;
  if (entry?.status === "failed") throw entry.error;
  const client = (entry ? entry.client : source) as TClient;
  const subscribe = useCallback((onChange: () => void) => client.subscribe(() => onChange()), [client]);
  const read = useCallback(() => client.snapshot as TClient["snapshot"], [client]);
  const state = useSyncExternalStore(subscribe, read, read);
  return { state, client };
}

