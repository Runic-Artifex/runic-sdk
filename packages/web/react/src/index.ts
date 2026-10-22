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
  createCommandController,
  createLatestOperationController,
  createOperationController,
  createViewController,
  onBridgeConnection,
  type BridgeConnectionOptions,
  type BridgeConnectionState,
  type BridgeOperation,
  type BridgeOutcomeFailure,
  type LatestOperationController,
  type LatestOperationState,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
  type PageReference,
  type ViewClient,
  type ViewController,
  type ViewControllerState,
  type ViewSource,
} from "@runic-artifex/views";
import { isViewClient, viewSourceIdentity } from "@runic-artifex/views/bindings";

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

type ViewStore<TClient extends ViewClient> = {
  readonly subscribe: (onChange: () => void) => () => void;
  readonly read: () => ViewControllerState<TClient>;
  /** Takes the controller's state after `setSource` when what the hook returns changed. */
  readonly refresh: () => void;
};

// The controller changes `current` silently when the effect sets its source.
// The store copies it and re-renders only when what the hook returns changes,
// so an inline connect function cannot re-render synchronously forever, and a
// returned client is never one the controller already released.
function createViewStore<TClient extends ViewClient>(controller: ViewController<TClient>): ViewStore<TClient> {
  let snapshot = controller.current;
  const listeners = new Set<() => void>();
  const visible = (left: ViewControllerState<TClient>, right: ViewControllerState<TClient>) =>
    left.client !== right.client || left.state !== right.state || left.error !== right.error || left.pending !== right.pending;
  // React re-renders whenever the snapshot object changes, so keep the copy
  // unless what the hook returns changes. A copy with the same client, state,
  // error and pending is equivalent: a released client is never current again.
  const refresh = () => {
    const next = controller.current;
    if (next === snapshot || !visible(next, snapshot)) return;
    snapshot = next;
    for (const listener of [...listeners]) listener();
  };
  controller.subscribe(() => {
    snapshot = controller.current;
    for (const listener of [...listeners]) listener();
  });
  return {
    subscribe(onChange) {
      listeners.add(onChange);
      // Pick up changes made before React subscribed, such as a fast connection.
      refresh();
      return () => { listeners.delete(onChange); };
    },
    read: () => snapshot,
    refresh,
  };
}

export interface UseViewOptions<TClient extends ViewClient> {
  /** Releases a client this hook connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
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
export function useView<TClient extends ViewClient>(source: ViewSource<TClient>, options: UseViewOptions<TClient> = {}): ViewHandle<TClient> {
  const release = useRef(options.release);
  useInsertionEffect(() => { release.current = options.release; });
  const [controller] = useState(() => createViewController<TClient>({
    release: client => { if (release.current) release.current(client); else client.dispose(); },
  }));
  const [store] = useState(() => createViewStore(controller));
  const tracker = useRef<InlineConnectTracker>({ connect: undefined, text: "", times: [], warned: false });
  const identity = viewSourceIdentity(source);
  useEffect(() => {
    if (process.env.NODE_ENV !== "production" && source && !isViewClient(source)) warnInlineConnect(tracker.current, source.connect);
    controller.setSource(source);
    store.refresh();
    // A following run refreshes the store; after unmount nothing renders it.
    return () => { controller.setSource(null); };
    // The source is identified by its client or connect function, not by its wrapper object.
  }, [controller, store, identity]);
  const current = useSyncExternalStore(store.subscribe, store.read, store.read);
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

/**
 * A build-known map checks each generated reference kind against its component's `page` prop. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: ComponentType<{ readonly page: Extract<NonNullable<R>, { readonly kind: K }> }>;
};

export interface ViewOutletProps<R extends PageReference> {
  /** The presented reference, such as `state.main`. */
  readonly content: R | null | undefined;
  readonly registry: ViewRegistry<R>;
  /** Rendered while there is no content. Defaults to nothing. */
  readonly fallback?: ReactNode;
  /**
   * Renders a reference whose kind has no registered component, for example localized text.
   * Defaults to an English `<p role="alert">`.
   */
  readonly missing?: (content: R) => ReactNode;
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
 * component. A kind without a component renders `missing`, or an English alert without it.
 */
export function ViewOutlet<R extends PageReference>({ content, registry, fallback = null, missing }: ViewOutletProps<R>): ReactNode {
  if (!content) return fallback;
  const component = registry[content.kind as R["kind"]] as ComponentType<{ readonly page: R }> | undefined;
  if (!component) {
    if (missing) return missing(content);
    return createElement("p", { role: "alert" }, `No web component is registered for ${content.kind}.`);
  }
  return createElement(component, { key: referenceKey(content), page: content });
}

// A command or operation controller lives while its component is mounted: unmounting disposes it,
// which stops its feedback and, for an operation, settles its pending run promises and stops
// observing accepted work (it never cancels the work). StrictMode's simulated unmount, and a component that React shows again (`<Activity>`),
// mount once more with a fresh controller, so feedback of runs started before the unmount ends.
const disposedControllers = new WeakSet<object>();

function useMountedController<T extends { dispose(): void }>(create: () => T): T {
  const controller = useRef<T | null>(null);
  if (controller.current === null) controller.current = create();
  const [, remount] = useReducer((count: number) => count + 1, 0);
  useEffect(() => {
    if (disposedControllers.has(controller.current!)) {
      controller.current = create();
      remount();
    }
    const mounted = controller.current!;
    return () => {
      disposedControllers.add(mounted);
      mounted.dispose();
    };
    // The controller reads the latest callbacks through refs, so it is created once per mount.
  }, []);
  return controller.current;
}

export interface CommandHandle<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  /**
   * Runs the command. Resolves to its result, including a `BridgeOutcome` with a
   * declared failure, which `failure` then holds, or to undefined after an
   * unexpected failure, which `error` then holds. Never rejects.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** Why the latest run failed unexpectedly, until the next run starts. */
  readonly error: unknown;
  /**
   * The declared failure of the latest run, for a command that resolves a
   * `BridgeOutcome`, until the next run starts. A superseded run sets neither.
   */
  readonly failure: TFailure | undefined;
  /** Clears `error` and `failure`. */
  reset(): void;
}

/**
 * Tracks a command's pending state and failure for rendering. `run` always
 * calls the latest `command`, so it may close over the current client. Unmounting
 * disposes the controller: `pending`, `error` and `failure` stop updating, while a
 * run still in flight resolves as usual.
 *
 * ```tsx
 * const increment = useCommand(() => client?.increment());
 * <button disabled={!client || increment.pending} onClick={() => increment.run()}>…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandHandle<TArgs, Awaited<TReturn>> {
  const latest = useRef(command);
  useInsertionEffect(() => { latest.current = command; });
  const controller = useMountedController(() => createCommandController((...args: TArgs) => latest.current(...args)));
  const read = useCallback(() => controller.current, [controller]);
  const { pending, error, failure } = useSyncExternalStore(controller.subscribe, read, read);
  return { run: controller.run, pending, error, failure, reset: controller.reset };
}

export interface OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never>
  extends OperationState<TResult, TFailure>, Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset"> {}

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback of a
 * generated `start<Command>()` operation for rendering. `run` always calls the
 * latest `start` and options, so they may close over the current client. A run
 * that ends `cancelled` sets `cancelled`, not `error`. Unmounting disposes the
 * controller: a run still in flight resolves to undefined and `waitForCompletion`
 * is not called after it, but accepted work is never cancelled.
 *
 * ```tsx
 * const push = useOperation((remote: string) => client?.startPush(remote));
 * <button disabled={!client || push.pending} onClick={() => push.run("origin")}>Push</button>
 * {push.pending && <button disabled={push.cancellationRequested} onClick={() => push.cancel()}>Cancel</button>}
 * {push.cancelled && <p role="status">Push cancelled.</p>}
 * ```
 */
export function useOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  const latest = useRef({ start, options });
  useInsertionEffect(() => { latest.current = { start, options }; });
  const controller = useMountedController(() => createOperationController((...args: TArgs) => latest.current.start(...args), {
    cancel: operation => latest.current.options.cancel ? latest.current.options.cancel(operation) : operation.cancel(),
    waitForCompletion: (operation, status) => latest.current.options.waitForCompletion?.(operation, status),
  }));
  const read = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, read, read);
  return { ...current, run: controller.run, cancel: controller.cancel, reset: controller.reset };
}

export interface LatestOperationHandle<TResult, TFailure = never>
  extends LatestOperationState<TResult, TFailure>, Pick<LatestOperationController<TResult, TFailure>, "run" | "clear"> {}

/**
 * Admits only the newest selection, for reads such as "load the selected item": each
 * `run(intent)` replaces the queued intent and cancels the active read, and the next read
 * starts only after the previous one has finished (including `waitForCompletion`). The
 * intent carries `start`, `cancel`, `waitForCompletion` and `isCurrent`, so it may close over
 * the current client. `blocked` means admission is uncertain; only `clear("replace")`, after
 * the application has established a fresh session, clears it. Unmounting disposes the
 * controller: pending runs resolve to undefined, but accepted work is never cancelled.
 *
 * ```tsx
 * const load = useLatestOperation<Item>();
 * <li onClick={() => load.run({ start: () => client?.startLoad(id) })}>…</li>
 * {load.outcome?.ok && <ItemView item={load.outcome.value} />}
 * ```
 */
export function useLatestOperation<TResult = void, TFailure = never>(): LatestOperationHandle<TResult, TFailure> {
  const controller = useMountedController(() => createLatestOperationController<TResult, TFailure>());
  const read = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, read, read);
  return { ...current, run: controller.run, clear: controller.clear };
}

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. */
  readonly state: BridgeConnectionState;
}

// The state before the component subscribes: a subscription reports it at once.
function currentBridgeConnection(options: BridgeConnectionOptions): BridgeConnectionState {
  let current: BridgeConnectionState = "connecting";
  onBridgeConnection(state => { current = state; }, options)();
  return current;
}

/**
 * Renders the page's connection to its .NET host (`connecting`, `connected`, `reconnecting`
 * or `lost`) while the component is mounted, through `onBridgeConnection`. A changed
 * `lostAfter` subscribes again. The server snapshot is `connecting`.
 *
 * ```tsx
 * const connection = useBridgeConnection();
 * {connection.state === "lost" && <p role="alert">The application stopped responding.</p>}
 * ```
 */
export function useBridgeConnection(options: BridgeConnectionOptions = {}): BridgeConnectionHandle {
  const { lostAfter } = options;
  const [store] = useState(() => ({ state: undefined as BridgeConnectionState | undefined }));
  const subscribe = useCallback((onChange: () => void) => onBridgeConnection(state => {
    store.state = state;
    onChange();
  }, lostAfter === undefined ? {} : { lostAfter }), [store, lostAfter]);
  const read = useCallback(() => store.state ??= currentBridgeConnection(lostAfter === undefined ? {} : { lostAfter }), [store, lostAfter]);
  const state = useSyncExternalStore(subscribe, read, serverConnectionState);
  return { state };
}

const serverConnectionState = (): BridgeConnectionState => "connecting";

export { retrySuspenseView, useSuspenseView, type SuspenseViewHandle, type SuspenseViewOptions } from "./suspense.js";
export { useCollectionViewport, type CollectionViewportHandle } from "./viewport.js";
