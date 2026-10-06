import {
  createElement,
  useCallback,
  useEffect,
  useInsertionEffect,
  useReducer,
  useRef,
  useState,
  useSyncExternalStore,
  type ComponentType,
  type ReactNode,
} from "react";
import {
  createCollectionViewportController,
  createCommandController,
  createViewController,
  isViewClient,
  viewSourceIdentity,
  type CollectionViewport,
  type CollectionViewportOptions,
  type ViewClient,
  type ViewConnector,
  type ViewReference,
  type ViewSource,
} from "@runic-artifex/views";

export type { CollectionViewport, CollectionViewportOptions, ViewClient, ViewConnector, ViewReference, ViewSource } from "@runic-artifex/views";

// React itself requires bundlers to define process.env.NODE_ENV.
declare const process: { readonly env: { readonly NODE_ENV?: string } };

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: TClient["snapshot"] | undefined;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: TClient | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** True while a reference is connecting. */
  readonly pending: boolean;
  /** Connects a reference again after a failure. */
  retry(): void;
}

type InlineConnectTracker = { connect: unknown; text: string; times: number[]; warned: boolean };

// An inline `{ connect: () => connectX() }` creates a new function on every
// render, so every render reconnects and each connection renders again.
function warnInlineConnect(tracker: InlineConnectTracker, connect: () => unknown): void {
  if (tracker.connect === connect) return;
  const text = Function.prototype.toString.call(connect);
  const now = Date.now();
  tracker.times = tracker.text === text ? [...tracker.times.filter(time => now - time < 1000), now] : [now];
  tracker.connect = connect;
  tracker.text = text;
  if (tracker.times.length < 4 || tracker.warned) return;
  tracker.warned = true;
  console.warn("useView reconnected repeatedly because its source's connect function is recreated on every render, " +
    "for example `useView({ connect: () => connectWorkspace() })`. Pass a stable function such as " +
    "`{ connect: connectWorkspace }` or a generated page reference, or memoize the source with useMemo.");
}

// The controller changes `current` silently when the effect sets its source, so
// React renders the last notified snapshot instead of re-rendering for it.
function useNotifiedSnapshot<T>(controller: { readonly current: T; subscribe(listener: () => void): () => void }): T {
  const [store] = useState(() => {
    let snapshot = controller.current;
    return {
      subscribe(onChange: () => void) {
        const stop = controller.subscribe(() => { snapshot = controller.current; onChange(); });
        // Pick up changes made before React subscribed, such as a fast connection.
        if (snapshot !== controller.current) { snapshot = controller.current; onChange(); }
        return stop;
      },
      read: () => snapshot,
    };
  });
  return useSyncExternalStore(store.subscribe, store.read, store.read);
}

/**
 * Connects a generated View reference for the lifetime of the component and
 * renders its state through `useSyncExternalStore`. A changed reference
 * disposes the previous client; StrictMode's extra mount disposes its
 * connection when it resolves.
 *
 * A reference is identified by its `connect` function, so an inline
 * `{ connect: connectWorkspace }` does not reconnect. In development a
 * `connect` function recreated on every render logs a warning.
 */
export function useView<TClient extends ViewClient>(source: ViewSource<TClient>): ViewHandle<TClient> {
  const [controller] = useState(() => createViewController<TClient>());
  const tracker = useRef<InlineConnectTracker>({ connect: undefined, text: "", times: [], warned: false });
  const identity = viewSourceIdentity(source);
  useEffect(() => {
    if (process.env.NODE_ENV !== "production" && source && !isViewClient(source)) warnInlineConnect(tracker.current, source.connect);
    controller.setSource(source);
    return () => { controller.setSource(null); };
    // The source is identified by its client or connect function, not by its wrapper object.
  }, [controller, identity]);
  const current = useNotifiedSnapshot(controller);
  const retry = useCallback(() => controller.retry(), [controller]);

  const observed = source && isViewClient(source) ? source : undefined;
  // A connected client is readable before the effect observes it.
  if (observed && current.client !== observed)
    return { state: observed.snapshot, client: observed, error: undefined, pending: false, retry };
  // Until the effect follows a changed reference, do not show the previous one.
  if (viewSourceIdentity(current.source) !== identity)
    return { state: undefined, client: undefined, error: undefined, pending: source !== null && source !== undefined, retry };
  return { state: current.state, client: current.client, error: current.error, pending: current.pending, retry };
}

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
  });
  suspended.set(key, entry);
  return entry;
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
 * boundary; resetting the boundary connects again.
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
  if (entry?.status === "failed") {
    // Keep the failure for React's immediate retry, then let a reset reconnect.
    const key = connector!.connect;
    setTimeout(() => { if (suspended.get(key) === entry) suspended.delete(key); }, 0);
    throw entry.error;
  }
  const client = (entry ? entry.client : source) as TClient;
  const subscribe = useCallback((onChange: () => void) => client.subscribe(() => onChange()), [client]);
  const read = useCallback(() => client.snapshot as TClient["snapshot"], [client]);
  const state = useSyncExternalStore(subscribe, read, read);
  return { state, client };
}

/** A build-known map checks each generated reference kind against its component's `page` prop. */
export type ViewRegistry<R extends ViewReference> = {
  readonly [K in R["kind"]]: ComponentType<{ readonly page: Extract<R, { readonly kind: K }> }>;
};

export interface ViewOutletProps<R extends ViewReference> {
  /** The presented reference, such as `state.main`. */
  readonly content: R | null | undefined;
  readonly registry: ViewRegistry<R>;
  /** Rendered while there is no content. Defaults to nothing. */
  readonly fallback?: ReactNode;
}

const referenceKeys = new WeakMap<object, number>();
let nextReferenceKey = 0;

function referenceKey(reference: object): number {
  let key = referenceKeys.get(reference);
  if (key === undefined) referenceKeys.set(reference, key = ++nextReferenceKey);
  return key;
}

/**
 * Renders the registered component for a generated View reference and passes
 * the reference as its `page` prop. A different reference remounts the
 * component. A kind without a component renders an alert.
 */
export function ViewOutlet<R extends ViewReference>({ content, registry, fallback = null }: ViewOutletProps<R>): ReactNode {
  if (!content) return fallback;
  const component = registry[content.kind as R["kind"]] as ComponentType<{ readonly page: R }> | undefined;
  if (!component) return createElement("p", { role: "alert" }, `No web component is registered for ${content.kind}.`);
  return createElement(component, { key: referenceKey(content), page: content });
}

export interface CommandHandle<TArgs extends readonly unknown[], TResult> {
  /** Runs the command. Resolves to its result, or to undefined after a failure, which `error` then holds. Never rejects. */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** Why the latest run failed, until the next run starts. */
  readonly error: unknown;
  /** Clears `error`. */
  reset(): void;
}

/**
 * Tracks a command's pending state and failure for rendering. `run` always
 * calls the latest `command`, so it may close over the current client:
 *
 * ```tsx
 * const increment = useCommand(() => client?.increment());
 * <button disabled={!client || increment.pending} onClick={() => increment.run()}>…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TResult>(
  command: (...args: TArgs) => TResult | PromiseLike<TResult>,
): CommandHandle<TArgs, Awaited<TResult>> {
  const latest = useRef(command);
  useInsertionEffect(() => { latest.current = command; });
  const [controller] = useState(() => createCommandController((...args: TArgs) => latest.current(...args)));
  const read = useCallback(() => controller.current, [controller]);
  const { pending, error } = useSyncExternalStore(controller.subscribe, read, read);
  return { run: controller.run, pending, error, reset: controller.reset };
}

export interface CollectionViewportHandle {
  /** The rows to request and the sizes to render. */
  readonly viewport: CollectionViewport;
  /** Attach to the scroll container: `<div ref={ref}>`. */
  readonly ref: (element: HTMLElement | null) => void;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET. Send `viewport.start` and `viewport.size` to the ViewModel from an
 * effect; the hook only measures.
 */
export function useCollectionViewport(options: CollectionViewportOptions): CollectionViewportHandle {
  const [controller] = useState(() => createCollectionViewportController(options));
  const { totalCount, rowHeight, overscan } = options;
  useEffect(() => {
    controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) });
  }, [controller, totalCount, rowHeight, overscan]);
  const read = useCallback(() => controller.current, [controller]);
  const viewport = useSyncExternalStore(controller.subscribe, read, read);
  const ref = useCallback((element: HTMLElement | null) => controller.attach(element), [controller]);
  return { viewport, ref };
}
