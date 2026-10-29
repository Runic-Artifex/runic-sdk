/// <reference lib="esnext.disposable" preserve="true" />
import type { ViewClient } from "./connection.js";
import { disposable } from "./lifetime.js";
import { isBridgeOutcome, type BridgeOutcome } from "./outcome.js";
import { adoptConnection, checkKeepAlive, keepConnection, peekConnection } from "./keep-alive.js";

/** Anything that connects a client: a generated page reference or `{ connect: connect<Name> }`. */
export interface ViewConnector<TClient> {
  connect(): Promise<TClient>;
}

/**
 * What a framework binding follows. A connector is connected and released by
 * the binding; an already connected client is only observed, and its owner
 * disposes it.
 */
export type ViewSource<TClient extends ViewClient> = ViewConnector<TClient> | TClient | null | undefined;

/** One consistent reading of a {@link ViewController}. A new object is published for every change. */
export interface ViewControllerState<TClient extends ViewClient> {
  /** The source this state belongs to. */
  readonly source: ViewSource<TClient>;
  /** The connected or observed client, or undefined until it connects. */
  readonly client: TClient | undefined;
  /**
   * The latest state, or undefined until the client connects. While the same source connects again, after `retry()`
   * or once a released client's grace period ended, it stays the last state the controller showed, with `pending`.
   */
  readonly state: TClient["snapshot"] | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** True while a connector is connecting. */
  readonly pending: boolean;
}

export interface ViewControllerOptions<TClient extends ViewClient> {
  /** Releases a client this controller connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
  /**
   * Milliseconds a client this controller connected stays connected after the controller lets go of it (`release`,
   * `dispose` or a changed source), for the next controller that follows the same connector: a binding that mounts
   * again, such as after StrictMode's simulated unmount, Fast Refresh or HMR, `<Activity>` or A→B→A navigation,
   * takes it over with its state instead of opening the route again. A whole number from 0 to 2147483647; `0`
   * releases at once. Defaults to 2000. Connectors are identified by their `connect` function, as by `setSource`.
   */
  readonly keepAlive?: number;
}

/**
 * The connect, observe and release state machine that every framework binding
 * shares. A binding feeds it the current source and renders `current`.
 */
export interface ViewController<TClient extends ViewClient> {
  readonly current: ViewControllerState<TClient>;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Follows a source. A source with the same identity as the current one (the
   * same client, or a connector with the same `connect` function) is ignored,
   * so an inline `{ connect: connectWorkspace }` does not reconnect. Returns
   * whether the controller started following a different source.
   *
   * A connector whose client another controller let go of within its `keepAlive`
   * period takes that client over, with its state at once and without `pending`.
   *
   * `setSource` does not notify subscribers; read `current` after calling it.
   * Later changes (the connection settling, pushed state, `retry`) notify.
   */
  setSource(source: ViewSource<TClient>): boolean;
  /**
   * Lets go of the client for `keepAlive` milliseconds and stops following it, but keeps `current`, so a binding
   * that is unmounted or hidden shows its last state again at once. The next `setSource`, also with the same source,
   * follows it again and takes the kept client back. Does not notify.
   */
  release(): void;
  /** Connects the current source again, for example after a failure. */
  retry(): void;
  /**
   * Lets go of the current client, as `release` does, and stops publishing. A disposed controller ignores later
   * sources.
   */
  dispose(): void;
}

/** True for a connected client; false for a connector. */
export function isViewClient<TClient extends ViewClient>(source: ViewConnector<TClient> | TClient): source is TClient {
  return typeof (source as Partial<ViewClient>).subscribe === "function";
}

/** The identity a binding compares to decide whether a source changed: the client itself or the connect function. */
export function viewSourceIdentity<TClient extends ViewClient>(source: ViewSource<TClient>): object | undefined {
  if (!source) return undefined;
  return isViewClient(source) ? source : source.connect;
}

/**
 * What a binding shows for a source before its controller follows it, as in the render before an effect: an
 * observed client and its state, or the client and state that a released controller kept for the connector.
 * Undefined when the source has neither.
 */
export function viewSourcePreview<TClient extends ViewClient>(source: ViewSource<TClient>):
  { readonly client: TClient; readonly state: TClient["snapshot"] } | undefined {
  if (!source) return undefined;
  const client = isViewClient(source) ? source : peekConnection<TClient>(source);
  return client ? { client, state: client.snapshot } : undefined;
}

export interface PreloadViewOptions {
  /** Milliseconds the client waits for a binding to take it over before it is released. Defaults to 2000. */
  readonly keepAlive?: number;
}

/**
 * Connects a connector ahead of the component that shows it, such as in a router's `load` function, and keeps the
 * client for `keepAlive` milliseconds: the first `useView` / `injectView` that follows the connector takes it over
 * with its state, so the component renders the View at once instead of "Connecting…". Resolves with `source`, the
 * value to hand to the component, once the client connected; rejects with the connection's failure, so the router
 * shows its error page. A client that no binding takes over is released after `keepAlive`.
 *
 * ```ts
 * // SvelteKit, src/routes/editor/+page.ts
 * export const ssr = false;
 * export const load = async () => ({ editor: await preloadView({ connect: connectEditor }) });
 * ```
 */
export async function preloadView<TSource extends ViewConnector<ViewClient>>(source: TSource, options: PreloadViewOptions = {}): Promise<TSource> {
  const keepAlive = checkKeepAlive(options.keepAlive);
  const client = await source.connect();
  keepConnection(source, Promise.resolve(client), client, keepAlive === 0 ? 1 : keepAlive, connected => connected.dispose());
  return source;
}

/**
 * Creates the framework-neutral controller behind `useView` and `injectView`.
 * A `using` declaration disposes it where the engine has `Symbol.dispose`.
 */
export function createViewController<TClient extends ViewClient>(options: ViewControllerOptions<TClient> = {}): ViewController<TClient> & Disposable {
  const release = options.release ?? ((client: TClient) => client.dispose());
  const keepAlive = checkKeepAlive(options.keepAlive);
  const listeners = new Set<() => void>();
  let current: ViewControllerState<TClient> = { source: undefined, client: undefined, state: undefined, error: undefined, pending: false };
  let identity: object | undefined;
  // The identity `current.state` belongs to, so connecting the same source again keeps it.
  let stateIdentity: object | undefined;
  let stop: (() => void) | undefined;
  let released = false;
  let disposed = false;

  let quiet = false;
  function publish(next: Partial<ViewControllerState<TClient>>): void {
    current = { ...current, ...next };
    if (!quiet) for (const listener of [...listeners]) listener();
  }

  function start(): void {
    stop?.();
    stop = undefined;
    released = false;
    const source = current.source;
    if (!source) {
      stateIdentity = undefined;
      publish({ client: undefined, state: undefined, error: undefined, pending: false });
      return;
    }
    const followed = identity;
    let active = true;
    let unsubscribe: (() => void) | undefined;
    const observe = (client: TClient) => {
      stateIdentity = followed;
      publish({ client, state: client.snapshot, error: undefined, pending: false });
      unsubscribe = client.subscribe(state => { if (active && state !== current.state) publish({ state }); });
    };
    if (isViewClient(source)) {
      stop = () => { active = false; unsubscribe?.(); };
      observe(source);
      return;
    }
    const kept = adoptConnection<TClient>(source);
    let connecting: Promise<TClient>;
    if (kept) connecting = kept.connecting;
    else {
      try { connecting = source.connect(); }
      catch (cause) { connecting = Promise.reject(cause); }
    }
    let connected = kept?.client;
    let settled = connected !== undefined;
    stop = () => {
      active = false;
      unsubscribe?.();
      // Kept for the next controller that follows this connector; a failed connection has nothing to keep.
      if (connected || !settled) keepConnection(source, connecting, connected, keepAlive, release);
    };
    if (connected) { observe(connected); return; }
    // The same source connecting again keeps showing its last state.
    publish({ client: undefined, state: stateIdentity === followed ? current.state : undefined, error: undefined, pending: true });
    connecting.then(client => {
      settled = true;
      if (!active) return;
      connected = client;
      observe(client);
    }, (cause: unknown) => {
      settled = true;
      if (active) publish({ error: cause, pending: false });
    });
  }

  return disposable<ViewController<TClient>>({
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setSource(source) {
      if (disposed) return false;
      const next = viewSourceIdentity(source);
      if (next === identity && !released) {
        // Keep the newest wrapper object so `current.source` reflects what the binding passed.
        if (source !== current.source) current = { ...current, source };
        return false;
      }
      const changed = next !== identity;
      identity = next;
      current = { ...current, source };
      // The caller reads `current` itself. Not notifying keeps a render-driven
      // binding from rendering again for a change it is making during an update.
      quiet = true;
      try { start(); } finally { quiet = false; }
      return changed;
    },
    release() {
      if (disposed || released) return;
      released = true;
      stop?.();
      stop = undefined;
    },
    retry() {
      if (!disposed) start();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      stop?.();
      stop = undefined;
      listeners.clear();
    },
  });
}

/** One consistent reading of a {@link CommandController}. */
export interface CommandState<TFailure = never> {
  /** True while at least one run is in flight. */
  readonly pending: boolean;
  /**
   * Why the most recently started run failed unexpectedly, or undefined while it
   * runs or after it succeeded. A declared failure is `failure` instead.
   */
  readonly error: unknown;
  /**
   * The declared failure of the most recently started run, for a command that
   * resolves a `BridgeOutcome`; undefined while it runs or after it succeeded.
   */
  readonly failure: TFailure | undefined;
}

type OutcomeOf<T> = Extract<Extract<NonNullable<T>, BridgeOutcome<unknown, unknown>>, { readonly ok: false }>;
/** The declared failure type of a command result: `F` for a `BridgeOutcome<_, F>`, otherwise `never`. */
export type BridgeOutcomeFailure<T> = [OutcomeOf<T>] extends [never] ? never
  : OutcomeOf<T> extends { readonly failure: infer F } ? F : never;

/** Runs a command and tracks whether it is pending and why it last failed. */
export interface CommandController<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  readonly current: CommandState<TFailure>;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Runs the command. Starting a run clears `error` and `failure`. Resolves to
   * the command's result, including a `BridgeOutcome` with a declared failure,
   * or to `undefined` after an unexpected failure. `error` and `failure` are
   * set only if no later run started meanwhile. It never rejects, so callers
   * need no try/catch.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /**
   * Clears `error` and `failure`. Runs still in flight count as superseded:
   * they settle without setting either, as when a later run starts.
   */
  reset(): void;
  /** Stops publishing; runs still in flight settle without updating state. */
  dispose(): void;
}

/**
 * Creates the framework-neutral command runner behind `useCommand` and
 * `injectCommand`. The command may return a plain value or `undefined`, for
 * example `() => client?.increment()` while the client is still connecting.
 * It may also return one of several commands' promises, such as
 * `name => name === "save" ? client.save() : client.discard()`; the result is
 * then their union and `failure` the union of their declared failures.
 * A `using` declaration disposes it where the engine has `Symbol.dispose`.
 */
export function createCommandController<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandController<TArgs, Awaited<TReturn>, BridgeOutcomeFailure<Awaited<TReturn>>> & Disposable {
  type TFailure = BridgeOutcomeFailure<Awaited<TReturn>>;
  const listeners = new Set<() => void>();
  let current: CommandState<TFailure> = { pending: false, error: undefined, failure: undefined };
  let running = 0;
  let latest = 0;
  let disposed = false;

  function publish(next: Partial<CommandState<TFailure>>): void {
    if (disposed) return;
    current = { ...current, ...next };
    for (const listener of [...listeners]) listener();
  }

  return disposable<CommandController<TArgs, Awaited<TReturn>, TFailure>>({
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    async run(...args): Promise<Awaited<TReturn> | undefined> {
      running++;
      const run = ++latest;
      publish({ pending: true, error: undefined, failure: undefined });
      let failure: { readonly cause: unknown } | undefined;
      let declared: { readonly failure: TFailure } | undefined;
      try {
        const result = await command(...args);
        if (isBridgeOutcome(result) && !result.ok) declared = { failure: result.failure as TFailure };
        return result;
      } catch (cause) {
        failure = { cause };
        return undefined;
      } finally {
        running--;
        // A run superseded by a later one reports neither its failure nor its
        // declared failure, so an older run cannot replace a newer outcome.
        if (failure && run === latest) publish({ pending: running !== 0, error: failure.cause });
        else if (declared && run === latest) publish({ pending: running !== 0, failure: declared.failure });
        else if (running === 0) publish({ pending: false });
      }
    },
    reset() {
      latest++;
      if (current.error !== undefined || current.failure !== undefined) publish({ error: undefined, failure: undefined });
    },
    dispose() {
      disposed = true;
      listeners.clear();
    },
  });
}
