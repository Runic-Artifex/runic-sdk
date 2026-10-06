import type {
  BridgeOperation, BridgeOperationStatus, CollectionViewport, CollectionViewportController, ViewClient, ViewConnector,
} from "@runic-artifex/views";
import * as Duration from "effect/Duration";
import * as Effect from "effect/Effect";
import * as Exit from "effect/Exit";
import * as Queue from "effect/Queue";
import type * as Schedule from "effect/Schedule";
import type * as Scope from "effect/Scope";
import * as Stream from "effect/Stream";
import {
  bridgeFailure, ViewOperationCancelled, ViewOperationFailed, ViewOperationTimedOut, ViewOperationUncertain,
  type ViewError, type ViewOperationError,
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
 * Runs a command, setter, query or checked write. Bridge failures become tagged
 * errors; anything else, such as a `RangeError` for an invalid argument, is a defect.
 * Interruption abandons the reply only: .NET keeps running a command it received.
 *
 * ```ts
 * yield* command(() => editor.save());
 * ```
 */
export function command<A>(run: () => PromiseLike<A>): Effect.Effect<A, ViewError> {
  return attempt(run, viewFailure);
}

/** The success value of an operation: its result, or `void` for a command without one. */
export type OperationResult<T> = [T] extends [never] ? void : T;

export interface OperationOptions {
  /** Cancels the operation and fails with `ViewOperationTimedOut` when it does not finish in time. */
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
}

const transient = (error: ViewOperationError) =>
  error._tag === "ViewDisconnected" || error._tag === "ViewBridgeTimeout" || error._tag === "ViewOperationUncertain";

/**
 * Starts a .NET operation and waits for its terminal status. Interrupting the
 * Effect, including through `Effect.timeout`, sends the cancellation request
 * before the interruption completes.
 *
 * Retrying is only offered with an explicit request ID, which makes a repeated
 * start return the operation already running instead of starting another:
 *
 * ```ts
 * yield* operation(() => editor.startSave(), { timeout: "10 seconds" });
 * yield* operation(id => editor.startSaveWithRequestId(id), {
 *   requestId: crypto.randomUUID(),
 *   retry: Schedule.exponential("200 millis").pipe(Schedule.take(3)),
 * });
 * ```
 */
export function operation<T>(start: (requestId: string) => PromiseLike<BridgeOperation<T>>, options: RetryOperationOptions):
  Effect.Effect<OperationResult<T>, ViewOperationError>;
export function operation<T>(start: () => PromiseLike<BridgeOperation<T>>, options?: OperationOptions):
  Effect.Effect<OperationResult<T>, ViewOperationError>;
export function operation<T>(start: (requestId: string) => PromiseLike<BridgeOperation<T>>,
  options: OperationOptions & Partial<RetryOperationOptions> = {}): Effect.Effect<OperationResult<T>, ViewOperationError> {
  const once = runOperation(() => start(options.requestId ?? ""), options.timeout);
  return options.requestId === undefined || options.retry === undefined
    ? once
    : Effect.retry(once, { schedule: options.retry, while: transient });
}

function runOperation<T>(start: () => PromiseLike<BridgeOperation<T>>, timeout: Duration.Input | undefined):
  Effect.Effect<OperationResult<T>, ViewOperationError> {
  return Effect.acquireUseRelease(
    attempt(start, bridgeFailure).pipe(Effect.map(handle => ({ handle, settled: false }))),
    started => {
      const { handle } = started;
      const completed = attempt(() => handle.completion, bridgeFailure).pipe(
        Effect.tap(() => Effect.sync(() => { started.settled = true; })),
        Effect.flatMap(status => fromStatus(status)),
      );
      if (timeout === undefined) return completed;
      const duration = Duration.fromInputUnsafe(timeout);
      return completed.pipe(Effect.timeoutOrElse({
        duration,
        orElse: () => cancel(handle).pipe(Effect.flatMap(cancellation => {
          started.settled = true;
          const timedOut = Effect.fail(new ViewOperationTimedOut({
            message: `The operation ${handle.requestId} did not complete within ${Duration.format(duration)}; cancellation: ${cancellation}.`,
            requestId: handle.requestId, timeout: duration, cancellation,
          }));
          // `not-running`: it finished just before the deadline, so report how.
          if (cancellation !== "not-running") return timedOut;
          return Effect.tryPromise({ try: () => handle.status(), catch: cause => cause }).pipe(
            Effect.matchEffect({ onFailure: () => timedOut, onSuccess: status => status.kind === "running" ? timedOut : fromStatus(status) }));
        })),
      }));
    },
    // A failed observation leaves the operation running, so a retry with the
    // same request ID can still observe it. Only giving up cancels it.
    (started, exit) => Exit.hasInterrupts(exit) && !started.settled ? Effect.asVoid(cancel(started.handle)) : Effect.void,
  );
}

function cancel(handle: BridgeOperation<unknown>) {
  return Effect.tryPromise({ try: () => handle.cancel(), catch: cause => cause }).pipe(
    Effect.map(result => result.kind),
    Effect.orElseSucceed(() => "unobserved" as const),
  );
}

function fromStatus<T>(status: BridgeOperationStatus<T>): Effect.Effect<OperationResult<T>, ViewOperationError> {
  const { requestId } = status;
  switch (status.kind) {
    case "succeeded":
      if (status.delivery !== undefined)
        return Effect.fail(new ViewOperationFailed({ message: status.delivery.message, requestId, status, detail: undefined }));
      return Effect.succeed(status.result as OperationResult<T>);
    case "failed":
      return Effect.fail(new ViewOperationFailed({
        message: status.error?.message ?? `The operation ${requestId} failed.`, requestId, status, detail: status.error?.detail,
      }));
    case "cancelled":
      return Effect.fail(new ViewOperationCancelled({ message: `The operation ${requestId} was cancelled.`, requestId, status }));
    default:
      return Effect.fail(new ViewOperationUncertain({
        message: `The outcome of operation ${requestId} is ${status.kind}.`, requestId, cause: status,
      }));
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
