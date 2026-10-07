import {
  isBridgeOutcome, type BridgeOperation, type BridgeOperationStatusOf, type BridgeOutcome, type BridgeOutcomeFailure,
  type CollectionViewport, type CollectionViewportController, type ViewClient, type ViewConnector,
} from "@runic-artifex/views";
import * as Duration from "effect/Duration";
import * as Effect from "effect/Effect";
import * as Exit from "effect/Exit";
import * as Queue from "effect/Queue";
import type * as Schedule from "effect/Schedule";
import type * as Scope from "effect/Scope";
import * as Stream from "effect/Stream";
import {
  bridgeFailure, ViewDomainFailure, ViewOperationCancelled, ViewOperationFailed, ViewOperationTimedOut, ViewOperationUncertain,
  type ViewDomainFailureOf, type ViewError, type ViewOperationError,
} from "./errors.js";

function attempt<A, E>(run: () => PromiseLike<A>, map: (cause: unknown) => E | undefined): Effect.Effect<A, E> {
  return Effect.tryPromise({ try: () => run(), catch: cause => cause }).pipe(
    Effect.catch(cause => {
      const failure = map(cause);
      return failure === undefined ? Effect.die(cause) : Effect.fail(failure);
    }),
  );
}

const viewFailure = (cause: unknown): ViewError | undefined => {
  const failure = bridgeFailure(cause);
  return failure?._tag === "ViewOperationUncertain" ? undefined : failure;
};

/**
 * Connects a generated client for the lifetime of the current `Scope` and
 * disposes it when the scope closes. Pass a generated `connect<Name>` function or
 * a page reference from a content property.
 *
 * ```ts
 * const editor = yield* connect(connectEditor);
 * ```
 */
export function connect<C extends ViewClient>(source: ViewConnector<C> | (() => PromiseLike<C>)): Effect.Effect<C, ViewError, Scope.Scope> {
  const open = typeof source === "function" ? source : () => source.connect();
  return Effect.acquireRelease(attempt(open, viewFailure), client => Effect.sync(() => client.dispose()));
}

export interface StatesOptions {
  /** States kept for a slow consumer before older ones are dropped. Defaults to 1, the latest state. */
  readonly bufferSize?: number;
}

/**
 * The client's current state, then each accepted state. States are snapshots,
 * so a slow consumer skips to the latest one. A state re-read after an unusable
 * change or a reconnect arrives as an ordinary state; the stream does not fail.
 * It never ends on its own: interrupt it, or close the scope that connected the client.
 */
export function states<S>(client: ViewClient<S>, options: StatesOptions = {}): Stream.Stream<S> {
  return Stream.callback<S>(queue => Effect.acquireRelease(
    Effect.sync(() => client.subscribe(state => { Queue.offerUnsafe(queue, state); })),
    unsubscribe => Effect.sync(unsubscribe),
  ), { bufferSize: options.bufferSize ?? 1, strategy: "sliding" });
}

/**
 * The success value of a command: for one that declares a failure, the value of
 * its `BridgeOutcome`, otherwise what the client method resolves to.
 */
export type CommandValue<A> = A extends BridgeOutcome<unknown, unknown>
  ? A extends { readonly ok: true; readonly value: infer V } ? V : never
  : A;

/**
 * Runs a command, setter, query or checked write. Bridge failures become tagged
 * errors; anything else, such as a `RangeError` for an invalid argument, is a defect.
 * A command that declares a failure succeeds with its value and fails with
 * `ViewDomainFailure` for its declared failure.
 * Interruption abandons the reply only: .NET keeps running a command it received.
 *
 * ```ts
 * yield* command(() => editor.save());
 * ```
 */
export function command<A>(run: () => PromiseLike<A>):
  Effect.Effect<CommandValue<A>, ViewError | ViewDomainFailureOf<BridgeOutcomeFailure<A>>> {
  return attempt(run, viewFailure).pipe(Effect.flatMap(value => {
    if (!isBridgeOutcome(value)) return Effect.succeed(value as CommandValue<A>);
    return value.ok ? Effect.succeed(value.value as CommandValue<A>)
      : Effect.fail(new ViewDomainFailure({ message: "The command failed with its declared failure.", failure: value.failure,
        requestId: undefined }) as ViewDomainFailureOf<BridgeOutcomeFailure<A>>);
  }));
}

export interface OperationOptions {
  /**
   * Bounds the wait for the terminal status, which starts once .NET answered the
   * start request. When it passes, the operation is cancelled and the Effect
   * fails with `ViewOperationTimedOut`.
   */
  readonly timeout?: Duration.Input;
}

export interface RetryOperationOptions extends OperationOptions {
  /** The request ID passed to every attempt, so .NET runs the operation at most once. */
  readonly requestId: string;
  /**
   * Retries a start or observation that failed with `ViewDisconnected`,
   * `ViewBridgeTimeout` or `ViewOperationUncertain`. Other failures, and an
   * operation that reached a terminal status, are not retried.
   */
  readonly retry?: Schedule.Schedule<unknown, ViewOperationError>;
  /** Further limits which of those failures are retried, for example to stop once a page is gone. */
  readonly while?: (error: ViewOperationError) => boolean;
}

const transient = (error: ViewOperationError) =>
  error._tag === "ViewDisconnected" || error._tag === "ViewBridgeTimeout" || error._tag === "ViewOperationUncertain";

// The operation the latest attempt started, shared by every attempt.
interface Started {
  handle: BridgeOperation<unknown, unknown> | undefined;
  settled: boolean;
}

/**
 * Starts a .NET operation and waits for its terminal status.
 *
 * When the Effect ends without a terminal status, because it was interrupted
 * (including by `Effect.timeout` or between retries), timed out, or failed to
 * observe the operation after its last attempt, it sends the cancellation
 * request for the operation it started before it completes. The start request
 * itself is not interruptible: an interruption waits for .NET to answer it, so
 * an admitted operation is never left running unobserved.
 *
 * Retrying is only offered with an explicit request ID, which makes a repeated
 * start return the operation already running instead of starting another.
 * Between attempts the operation keeps running:
 *
 * ```ts
 * yield* operation(() => editor.startSave(), { timeout: "10 seconds" });
 * yield* operation(id => editor.startSaveWithRequestId(id), {
 *   requestId: crypto.randomUUID(),
 *   retry: Schedule.exponential("200 millis").pipe(Schedule.take(3)),
 * });
 * ```
 */
export function operation<T, F = never>(start: (requestId: string) => PromiseLike<BridgeOperation<T, F>>, options: RetryOperationOptions):
  Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>>;
export function operation<T, F = never>(start: () => PromiseLike<BridgeOperation<T, F>>, options?: OperationOptions):
  Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>>;
export function operation<T, F>(start: (requestId: string) => PromiseLike<BridgeOperation<T, F>>,
  options: OperationOptions & Partial<RetryOperationOptions> = {}): Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>> {
  return Effect.suspend(() => {
    const started: Started = { handle: undefined, settled: true };
    const once = runOperation<T, F>(() => start(options.requestId ?? ""), options.timeout, started);
    const limit = options.while;
    const attempts = options.requestId === undefined || options.retry === undefined ? once
      // A declared failure is a terminal outcome, never transient, so it is not retried.
      : Effect.retry(once as Effect.Effect<T, ViewOperationError>,
        { schedule: options.retry, while: error => transient(error) && (limit === undefined || limit(error)) }) as typeof once;
    return attempts.pipe(Effect.onExit(exit =>
      Exit.isSuccess(exit) || started.handle === undefined || started.settled ? Effect.void : Effect.asVoid(cancel(started.handle))));
  });
}

function runOperation<T, F>(start: () => PromiseLike<BridgeOperation<T, F>>, timeout: Duration.Input | undefined, started: Started):
  Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>> {
  // The start is uninterruptible so its handle is always recorded for cancellation.
  return Effect.uninterruptibleMask(restore => attempt(start, bridgeFailure).pipe(
    Effect.tap(handle => Effect.sync(() => { started.handle = handle as BridgeOperation<unknown, unknown>; started.settled = false; })),
    Effect.flatMap(handle => restore(waitFor(handle, timeout, started))),
  ));
}

function waitFor<T, F>(handle: BridgeOperation<T, F>, timeout: Duration.Input | undefined, started: Started):
  Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>> {
  const completed = attempt(() => handle.completion, bridgeFailure).pipe(
    Effect.tap(() => Effect.sync(() => { started.settled = true; })),
    Effect.flatMap(status => fromStatus<T, F>(status)),
  );
  if (timeout === undefined) return completed;
  const duration = Duration.fromInputUnsafe(timeout);
  return completed.pipe(Effect.timeoutOrElse({
    duration,
    orElse: () => Effect.suspend(() => {
      // Cancelled here, so the caller's exit does not cancel it again.
      started.settled = true;
      return cancel(handle as BridgeOperation<unknown, unknown>);
    }).pipe(Effect.flatMap(cancellation => {
      const timedOut = Effect.fail(new ViewOperationTimedOut({
        message: `The operation ${handle.requestId} did not complete within ${Duration.format(duration)}; cancellation: ${cancellation}.`,
        requestId: handle.requestId, timeout: duration, cancellation,
      }));
      // `not-running`: it finished just before the deadline, so report how.
      if (cancellation !== "not-running") return timedOut;
      return Effect.tryPromise({ try: () => handle.status(), catch: cause => cause }).pipe(
        Effect.matchEffect({ onFailure: () => timedOut, onSuccess: status => status.kind === "running" ? timedOut : fromStatus<T, F>(status) }));
    })),
  }));
}

function cancel(handle: BridgeOperation<unknown, unknown>) {
  return Effect.tryPromise({ try: () => handle.cancel(), catch: cause => cause }).pipe(
    Effect.map(result => result.kind),
    Effect.orElseSucceed(() => "unobserved" as const),
  );
}

// Generic code reads the full status union; the public BridgeOperationStatus
// of an operation without a declared failure is one of its cases.
function fromStatus<T, F>(observed: unknown): Effect.Effect<T, ViewOperationError | ViewDomainFailureOf<F>> {
  const status = observed as BridgeOperationStatusOf<T, F>;
  const { requestId } = status;
  switch (status.kind) {
    case "succeeded":
      if (status.delivery !== undefined)
        return Effect.fail(new ViewOperationFailed({ message: status.delivery.message, requestId, status, detail: undefined }));
      return Effect.succeed(status.result as T);
    case "failed":
      return Effect.fail(new ViewOperationFailed({
        message: status.error?.message ?? `The operation ${requestId} failed.`, requestId, status, detail: status.error?.detail,
      }));
    // The declared failure. One the window could not retain arrives with a
    // delivery instead: the command failed as declared, but the value is lost.
    case "domain-failed":
      if (status.failure === undefined)
        return Effect.fail(new ViewOperationFailed({
          message: status.delivery?.message ?? `The operation ${requestId} failed.`, requestId, status, detail: undefined,
        }));
      return Effect.fail(new ViewDomainFailure({
        message: `The operation ${requestId} failed with its declared failure.`, failure: status.failure, requestId,
      }) as ViewDomainFailureOf<F>);
    case "cancelled":
      return Effect.fail(new ViewOperationCancelled({ message: `The operation ${requestId} was cancelled.`, requestId, status }));
    case "timedOut":
      return Effect.fail(new ViewOperationTimedOut({
        message: `The operation ${requestId} timed out.`, requestId, timeout: undefined, cancellation: status.cancellation ?? "unobserved",
      }));
    case "running":
    case "expired":
    case "unknown":
      return Effect.fail(new ViewOperationUncertain({
        message: `The outcome of operation ${requestId} is ${status.kind}.`, requestId, cause: status,
      }));
    default: {
      const unknown: never = status;
      return Effect.fail(new ViewOperationUncertain({
        message: `The outcome of operation ${requestId} is ${String((unknown as { readonly kind?: unknown }).kind)}.`, requestId, cause: unknown,
      }));
    }
  }
}

/** The rows a viewport request asks .NET for. */
export interface ViewportRange {
  readonly start: number;
  readonly size: number;
}

/** The controller's current viewport, then each change. A slow consumer skips to the latest one. */
export function viewportChanges(controller: CollectionViewportController): Stream.Stream<CollectionViewport> {
  return Stream.callback<CollectionViewport>(queue => Effect.acquireRelease(
    Effect.sync(() => {
      Queue.offerUnsafe(queue, controller.current);
      return controller.subscribe(() => { Queue.offerUnsafe(queue, controller.current); });
    }),
    unsubscribe => Effect.sync(unsubscribe),
  ), { bufferSize: 1, strategy: "sliding" });
}

/**
 * Sends each new row range of a viewport controller to .NET, interrupting the
 * request for a range the user already scrolled past. Pass a command, or an
 * {@link operation}, whose interruption cancels it in .NET:
 *
 * ```ts
 * yield* followViewport(controller, range => operation(() => rows.startSetViewport(range)));
 * ```
 *
 * Runs until interrupted, and fails with the first failure of `request`.
 */
export function followViewport<A, E, R>(controller: CollectionViewportController,
  request: (range: ViewportRange) => Effect.Effect<A, E, R>): Effect.Effect<void, E, R> {
  return viewportChanges(controller).pipe(
    Stream.map(({ start, size }): ViewportRange => ({ start, size })),
    Stream.changesWith((left, right) => left.start === right.start && left.size === right.size),
    Stream.switchMap(range => Stream.fromEffect(request(range))),
    Stream.runDrain,
  );
}
