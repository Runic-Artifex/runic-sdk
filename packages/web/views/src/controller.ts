import type { ViewClient } from "./connection.js";
import { isBridgeOutcome, type BridgeOutcome } from "./outcome.js";

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
  /** The latest state, or undefined until the client connects. */
  readonly state: TClient["snapshot"] | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** True while a connector is connecting. */
  readonly pending: boolean;
}

export interface ViewControllerOptions<TClient extends ViewClient> {
  /** Releases a client this controller connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
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
   * `setSource` does not notify subscribers; read `current` after calling it.
   * Later changes (the connection settling, pushed state, `retry`) notify.
   */
  setSource(source: ViewSource<TClient>): boolean;
  /** Connects the current source again, for example after a failure. */
  retry(): void;
  /** Releases the current client and stops publishing. A disposed controller ignores later sources. */
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

/** Creates the framework-neutral controller behind `useView` and `injectView`. */
export function createViewController<TClient extends ViewClient>(options: ViewControllerOptions<TClient> = {}): ViewController<TClient> {
  const release = options.release ?? ((client: TClient) => client.dispose());
  const listeners = new Set<() => void>();
  let current: ViewControllerState<TClient> = { source: undefined, client: undefined, state: undefined, error: undefined, pending: false };
  let identity: object | undefined;
  let stop: (() => void) | undefined;
  let disposed = false;

  let quiet = false;
  function publish(next: Partial<ViewControllerState<TClient>>): void {
    current = { ...current, ...next };
    if (!quiet) for (const listener of [...listeners]) listener();
  }

  function start(): void {
    stop?.();
    stop = undefined;
    const source = current.source;
    if (!source) {
      publish({ client: undefined, state: undefined, error: undefined, pending: false });
      return;
    }
    let active = true;
    let connected: TClient | undefined;
    let unsubscribe: (() => void) | undefined;
    stop = () => {
      active = false;
      unsubscribe?.();
      if (connected) release(connected);
    };
    const observe = (client: TClient) => {
      publish({ client, state: client.snapshot, error: undefined, pending: false });
      unsubscribe = client.subscribe(state => { if (active && state !== current.state) publish({ state }); });
    };
    if (isViewClient(source)) {
      observe(source);
      return;
    }
    publish({ client: undefined, state: undefined, error: undefined, pending: true });
    let connecting: Promise<TClient>;
    try { connecting = source.connect(); }
    catch (cause) { connecting = Promise.reject(cause); }
    connecting.then(client => {
      if (!active) { release(client); return; }
      connected = client;
      observe(client);
    }, (cause: unknown) => {
      if (active) publish({ error: cause, pending: false });
    });
  }

  return {
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setSource(source) {
      if (disposed) return false;
      const next = viewSourceIdentity(source);
      if (next === identity) {
        // Keep the newest wrapper object so `current.source` reflects what the binding passed.
        if (source !== current.source) current = { ...current, source };
        return false;
      }
      identity = next;
      current = { ...current, source };
      // The caller reads `current` itself. Not notifying keeps a render-driven
      // binding from rendering again for a change it is making during an update.
      quiet = true;
      try { start(); } finally { quiet = false; }
      return true;
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
  };
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
 */
export function createCommandController<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandController<TArgs, Awaited<TReturn>, BridgeOutcomeFailure<Awaited<TReturn>>> {
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

  return {
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
  };
}
