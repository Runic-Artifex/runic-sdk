import { useCallback, useEffect, useReducer, useSyncExternalStore } from "react";
import type { ViewClient, ViewConnector } from "@runic-artifex/views";
import { isViewClient } from "@runic-artifex/views/bindings";

// Experimental (`@runic-artifex/react/experimental`): the module-wide cache and its fixed ten-second
// timeout may change (docs/experimental.md).

type SuspendedConnection = {
  status: "pending" | "ready" | "failed";
  promise: Promise<void>;
  client?: ViewClient;
  error?: unknown;
  users: number;
  timer?: ReturnType<typeof setTimeout>;
};

// Suspense discards the state of a component that never mounted, so pending
// connections live here, keyed by connect function, until a component claims them.
const suspended = new WeakMap<object, SuspendedConnection>();
const unclaimedConnectionTimeout = 10_000;

function releaseLater(key: object, entry: SuspendedConnection, delay: number): void {
  clearTimeout(entry.timer);
  entry.timer = setTimeout(() => {
    if (entry.users !== 0 || suspended.get(key) !== entry) return;
    suspended.delete(key);
    entry.client?.dispose();
  }, delay);
}

function suspendedConnection(connector: ViewConnector<ViewClient>): SuspendedConnection {
  const key = connector.connect;
  const existing = suspended.get(key);
  if (existing) return existing;
  const entry: SuspendedConnection = { status: "pending", promise: Promise.resolve(), users: 0 };
  entry.promise = Promise.resolve().then(() => connector.connect()).then(client => {
    entry.status = "ready";
    entry.client = client;
    // Release a connection whose render was abandoned before it mounted.
    releaseLater(key, entry, unclaimedConnectionTimeout);
  }, (error: unknown) => {
    entry.status = "failed";
    entry.error = error;
    // Keep the failure for the error boundary; forget it later so a later render reconnects.
    entry.timer = setTimeout(() => { if (suspended.get(key) === entry) suspended.delete(key); }, unclaimedConnectionTimeout);
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
 * connect again. An unretried failure is forgotten after ten seconds.
 *
 * Components that pass a reference with the same `connect` function share one
 * client, which is disposed after the last of them unmounts. Pass a stable
 * function such as a generated page reference or `{ connect: connectWorkspace }`:
 * a function recreated on every render never resolves. An already connected
 * client is observed and left to its owner.
 */
export function useSuspenseView<TClient extends ViewClient>(source: ViewConnector<TClient> | TClient): SuspenseViewHandle<TClient> {
  const connector = isViewClient(source) ? undefined : source;
  const entry = connector ? suspendedConnection(connector) : undefined;
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

