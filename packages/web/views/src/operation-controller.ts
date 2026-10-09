import { emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError, BridgeOperationUncertainError } from "./errors.js";
import type { BridgeOutcome } from "./outcome.js";
import type { BridgeOperation, BridgeOperationCancelResult, BridgeOperationStatus } from "./operations.js";

type MaybePromise<T> = T | PromiseLike<T>;
type CancelResult = BridgeOperationCancelResult | void;

/** Feedback for the latest run; `pending` includes every run still being observed. */
export interface OperationState<TResult, TFailure = never> {
  readonly pending: boolean;
  /** The latest Start has not returned its operation handle yet. */
  readonly admitting: boolean;
  readonly operation: BridgeOperation<TResult, TFailure> | undefined;
  readonly status: BridgeOperationStatus<TResult, TFailure> | undefined;
  readonly outcome: BridgeOutcome<TResult, TFailure> | undefined;
  readonly failure: TFailure | undefined;
  readonly error: unknown;
  readonly cancellationRequested: boolean;
  /** A cancellation callback is in flight; its response does not establish completion. */
  readonly cancelling: boolean;
  readonly cancellation: BridgeOperationCancelResult | undefined;
  readonly cancelError: unknown;
}

export interface OperationControllerOptions<TResult, TFailure = never> {
  /** Defaults to the captured operation's `cancel()`. A model control command may return void. */
  readonly cancel?: (operation: BridgeOperation<TResult, TFailure>) => MaybePromise<CancelResult>;
  /**
   * An additional application completion/admission barrier, after `operation.wait()`.
   * Use when accepted work or command availability outlives the invocation wrapper.
   * This is not called after observation has been disposed.
   */
  readonly waitForCompletion?: (operation: BridgeOperation<TResult, TFailure>, status: BridgeOperationStatus<TResult, TFailure>) => MaybePromise<void>;
}

export interface OperationController<TArgs extends readonly unknown[], TResult, TFailure = never> {
  readonly current: OperationState<TResult, TFailure>;
  subscribe(listener: () => void): () => void;
  /** Starts and observes an operation. Never rejects; unexpected failures are `current.error`. */
  run(...args: TArgs): Promise<BridgeOutcome<TResult, TFailure> | undefined>;
  /** Retains Cancel before a delayed Start receipt. Never rejects; failures are `current.cancelError`. */
  cancel(): Promise<CancelResult>;
  /** Clears feedback; keeps pending work and its cancellation identity until completion. */
  reset(): void;
  /** Settles observation promises and detaches feedback. Does not cancel accepted work. */
  dispose(): void;
}

function initial<TResult, TFailure>(): OperationState<TResult, TFailure> {
  return { pending: false, admitting: false, operation: undefined, status: undefined, outcome: undefined,
    failure: undefined, error: undefined, cancellationRequested: false, cancelling: false, cancellation: undefined, cancelError: undefined };
}

function publisher<T>(value: T) {
  let current = value;
  const listeners = new Set<() => void>();
  return {
    get current() { return current; },
    publish(next: Partial<T>) {
      current = { ...current, ...next };
      for (const listener of [...listeners]) {
        try { listener(); } catch (error) { emitErrorDiagnostic(error); }
      }
    },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    dispose() { listeners.clear(); },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

/**
 * Framework-neutral operation feedback. Overlapping runs are observed independently;
 * only the latest publishes feedback or receives Cancel. No run is cancelled by
 * another run, reset or disposal. Applications normally disable mutation buttons
 * while pending, and use `createLatestOperationController` for superseding reads.
 */
export function createOperationController<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => MaybePromise<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationController<TArgs, TResult, TFailure> {
  interface Run {
    readonly result: ReturnType<typeof deferred<BridgeOutcome<TResult, TFailure> | undefined>>;
    readonly feedback: number;
    operation?: BridgeOperation<TResult, TFailure>;
    cancel?: ReturnType<typeof deferred<CancelResult>>;
    cancelStarted: boolean;
    settled: boolean;
  }
  const state = publisher(initial<TResult, TFailure>());
  const runs = new Set<Run>();
  const cancelling = new Set<Run>();
  let latest: Run | undefined;
  let feedback = 0;
  let disposed = false;
  const publish = (run: Run, next: Partial<OperationState<TResult, TFailure>>) => {
    if (!disposed && latest === run) state.publish(next);
  };

  async function cancel(run: Run): Promise<void> {
    if (disposed || run.settled || !run.operation || !run.cancel || run.cancelStarted) return;
    run.cancelStarted = true;
    publish(run, { cancelling: true });
    try {
      // Subscribers may detach ownership while reacting to cancellation feedback.
      if (disposed || run.settled) { run.cancel.resolve(undefined); return; }
      const result = await (options.cancel ? options.cancel(run.operation) : run.operation.cancel());
      publish(run, { cancellation: result === undefined ? undefined : result });
      run.cancel.resolve(result);
    } catch (error) {
      publish(run, { cancelError: error });
      run.cancel.resolve(undefined);
    } finally { cancelling.delete(run); publish(run, { cancelling: false }); }
  }

  async function execute(run: Run, args: TArgs): Promise<void> {
    try {
      if (disposed) return;
      const operation = await start(...args);
      if (disposed || !operation) return;
      run.operation = operation;
      publish(run, { admitting: false, operation });
      void cancel(run);
      if (disposed) return;
      const status = await operation.wait();
      if (disposed) return;
      publish(run, { status });
      if (disposed) return;
      await options.waitForCompletion?.(operation, status);
      if (disposed) return;
      const outcome = await operation.outcome();
      if (disposed) return;
      if (run.feedback === feedback) publish(run, { outcome, failure: outcome.ok ? undefined : outcome.failure });
      run.result.resolve(outcome);
    } catch (error) {
      if (run.feedback === feedback) publish(run, { error });
    } finally {
      run.settled = true;
      runs.delete(run);
      run.result.resolve(undefined);
      if (!run.cancelStarted) { run.cancel?.resolve(undefined); cancelling.delete(run); }
      if (!disposed) {
        if (latest === run) state.publish({ pending: runs.size !== 0, admitting: false });
        else state.publish({ pending: runs.size !== 0 });
      }
    }
  }

  return {
    get current() { return state.current; },
    subscribe(listener) { return disposed ? () => {} : state.subscribe(listener); },
    run(...args) {
      if (disposed) return Promise.resolve(undefined);
      const run: Run = { result: deferred(), feedback: ++feedback, cancelStarted: false, settled: false };
      latest = run;
      runs.add(run);
      state.publish({ ...initial<TResult, TFailure>(), pending: true, admitting: true });
      // execute handles every callback and transport rejection, including after disposal.
      void execute(run, args);
      return run.result.promise;
    },
    cancel() {
      const run = latest;
      if (disposed || !run || run.settled) return Promise.resolve(undefined);
      if (!run.cancel) {
        run.cancel = deferred();
        cancelling.add(run);
        state.publish({ cancellationRequested: true });
        void cancel(run);
      }
      return run.cancel.promise;
    },
    reset() {
      if (disposed) return;
      feedback++;
      state.publish({ outcome: undefined, failure: undefined, error: undefined, cancelError: undefined });
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      state.dispose();
      for (const run of runs) { run.result.resolve(undefined); run.cancel?.resolve(undefined); }
      for (const run of cancelling) run.cancel?.resolve(undefined);
      runs.clear();
      cancelling.clear();
    },
  };
}

/** A captured selection, including the connection/session against which it is valid. */
export interface LatestOperationIntent<TResult, TFailure = never> extends OperationControllerOptions<TResult, TFailure> {
  readonly start: () => MaybePromise<BridgeOperation<TResult, TFailure> | undefined>;
  /** Checked before Start, cancellation and publication. Capture the intent's session identity. */
  readonly isCurrent?: () => boolean;
}

export interface LatestOperationState<TResult, TFailure = never> extends OperationState<TResult, TFailure> {
  /** Completion/admission is uncertain. Only an explicit replacement clears this boundary. */
  readonly blocked: boolean;
}

export interface LatestOperationController<TResult, TFailure = never> {
  readonly current: LatestOperationState<TResult, TFailure>;
  subscribe(listener: () => void): () => void;
  /** Keeps only the newest queued intent. Never rejects; failures are `current.error`. */
  run(intent: LatestOperationIntent<TResult, TFailure>): Promise<BridgeOutcome<TResult, TFailure> | undefined>;
  /**
   * Drops queued intent. `cancel` requests cancellation of the captured current read;
   * `replace` detaches the old observation and suppresses cancellation because the
   * application has established a fresh session/admission boundary and owns the
   * old work's drain. Only `replace` clears uncertain admission.
   */
  clear(mode?: "cancel" | "replace"): void;
  /** Drops queued intent and settles observations, without cancelling accepted work. */
  dispose(): void;
}

/**
 * Serializes read admission, coalescing input received during delayed Start receipts
 * and cancellation. A cancellation response never admits the next read: the old
 * operation's terminal wait and optional application completion barrier must finish.
 */
export function createLatestOperationController<TResult = void, TFailure = never>(): LatestOperationController<TResult, TFailure> {
  interface Intent {
    readonly value: LatestOperationIntent<TResult, TFailure>;
    readonly result: ReturnType<typeof deferred<BridgeOutcome<TResult, TFailure> | undefined>>;
    readonly generation: number;
    superseded: boolean;
    suppressCancel: boolean;
    cancelStarted: boolean;
    cancellation?: Promise<void>;
    operation?: BridgeOperation<TResult, TFailure>;
  }
  const state = publisher<LatestOperationState<TResult, TFailure>>({ ...initial(), blocked: false });
  let pending: Intent | undefined;
  let active: Intent | undefined;
  let disposed = false;
  let replacement = 0;

  function dropPending(): void { pending?.result.resolve(undefined); pending = undefined; }

  function isCurrent(intent: Intent): boolean { return intent.value.isCurrent?.() ?? true; }
  function observed(intent: Intent): boolean { return !disposed && active === intent && intent.generation === replacement; }
  function fresh(intent: Intent): boolean { return observed(intent) && !intent.superseded && isCurrent(intent); }
  function publish(intent: Intent, next: Partial<LatestOperationState<TResult, TFailure>>) {
    if (fresh(intent)) state.publish(next);
  }

  async function cancel(intent: Intent): Promise<void> {
    try {
      if (disposed || intent.suppressCancel || intent.cancelStarted || !intent.operation || !isCurrent(intent)) return;
      intent.cancelStarted = true;
      if (active === intent) state.publish({ cancellationRequested: true, cancelling: true });
      // A subscriber may replace/dispose the session in response to this update.
      // Never issue a delayed application-global control against its fresh work.
      if (!observed(intent) || intent.suppressCancel || !isCurrent(intent)) return;
      const result = await (intent.value.cancel ? intent.value.cancel(intent.operation) : intent.operation.cancel());
      if (!disposed && active === intent && !intent.suppressCancel && isCurrent(intent))
        state.publish({ cancellation: result === undefined ? undefined : result });
    } catch (cancelError) {
      if (!disposed && active === intent && !intent.suppressCancel) state.publish({ cancelError });
    } finally {
      if (!disposed && active === intent) state.publish({ cancelling: false });
    }
  }

  function requestCancel(intent: Intent): void {
    if (!intent.operation || intent.cancellation) return;
    // Store the observation before publishing cancellation feedback, which can
    // reenter the controller. cancel consumes callback/transport failures.
    const completion = deferred<void>();
    intent.cancellation = completion.promise;
    void cancel(intent).then(() => { completion.resolve(); });
  }

  async function execute(intent: Intent): Promise<void> {
    let boundaryObserved = false;
    let startAttempted = false;
    try {
      if (!isCurrent(intent)) return;
      state.publish({ ...initial<TResult, TFailure>(), pending: true, admitting: true });
      if (!observed(intent) || !isCurrent(intent)) return;
      startAttempted = true;
      const operation = await intent.value.start();
      if (!observed(intent) || !operation) { boundaryObserved = true; return; }
      intent.operation = operation;
      if (isCurrent(intent)) state.publish({ admitting: false, operation });
      if (intent.superseded) requestCancel(intent);
      if (!observed(intent)) return;
      const status = await operation.wait();
      if (!observed(intent)) return;
      if (status.kind === "running" || status.kind === "unknown" || status.kind === "expired" || status.kind === "timedOut")
        throw new BridgeOperationUncertainError(status.contract, operation.requestId,
          "The previous operation's completion could not establish admission for the next selection.", { cause: status });
      await intent.value.waitForCompletion?.(operation, status);
      boundaryObserved = true;
      if (!observed(intent)) return;
      publish(intent, { status });
      if (!observed(intent)) return;
      const outcome = await operation.outcome();
      if (fresh(intent)) {
        state.publish({ outcome, failure: outcome.ok ? undefined : outcome.failure });
        if (fresh(intent)) intent.result.resolve(outcome);
      }
    } catch (error) {
      // A lost Start reply plus failed recovery can reject without a handle or a
      // typed uncertainty error. Only explicit rejected admission proves no work
      // was accepted; never infer that from an arbitrary transport/callback error.
      const rejected = error instanceof BridgeError && error.kind === "rejected";
      const uncertain = !boundaryObserved && (intent.operation !== undefined || (startAttempted && !rejected));
      if (observed(intent) && uncertain) {
        dropPending();
        state.publish({ blocked: true, error });
      } else if (observed(intent)) {
        // A throwing validity predicate is an ordinary callback failure, too.
        try { publish(intent, { error }); } catch (callbackError) { state.publish({ error: callbackError }); }
      }
    } finally {
      // Terminal/outcome subscribers can introduce supersession and a delayed
      // global control, too. Observe cancellation at the final handoff, after
      // every publication that still owns this intent. Skip an absent promise
      // synchronously so it cannot appear across an unnecessary await gap.
      if (observed(intent) && intent.cancellation) await intent.cancellation;
      intent.result.resolve(undefined);
      if (active === intent) {
        active = undefined;
        if (!disposed) state.publish({ pending: pending !== undefined, admitting: false, cancelling: false });
        pump();
      }
    }
  }

  function pump(): void {
    if (active || !pending || disposed || state.current.blocked) return;
    const intent = pending;
    pending = undefined;
    active = intent;
    // execute consumes late admission/wait/callback failures even after replacement.
    void execute(intent);
  }

  return {
    get current() { return state.current; },
    subscribe(listener) { return disposed ? () => {} : state.subscribe(listener); },
    run(value) {
      if (disposed || state.current.blocked) return Promise.resolve(undefined);
      pending?.result.resolve(undefined);
      const intent: Intent = { value, result: deferred(), generation: replacement, superseded: false, suppressCancel: false, cancelStarted: false };
      const previous = active;
      if (previous) previous.superseded = true;
      pending = intent;
      state.publish({ pending: true, outcome: undefined, failure: undefined, error: undefined, cancelError: undefined });
      // Publication can reenter run() and admit a newer intent. Only supersede
      // the active intent captured before publication, never that newer read.
      if (previous && observed(previous)) requestCancel(previous);
      pump();
      return intent.result.promise;
    },
    clear(mode = "cancel") {
      if (disposed) return;
      dropPending();
      if (active) {
        active.superseded = true;
        if (mode === "replace") {
          active.suppressCancel = true;
          active.result.resolve(undefined);
          active = undefined;
        }
        else requestCancel(active);
      }
      if (mode === "replace") { replacement++; state.publish({ ...initial<TResult, TFailure>(), blocked: false }); }
      else if (!active) state.publish({ pending: false });
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      state.dispose();
      pending?.result.resolve(undefined);
      active?.result.resolve(undefined);
      pending = undefined;
      if (active) { active.superseded = true; active.suppressCancel = true; }
    },
  };
}
