import { BridgeError, uncertainOperation, type BridgeFailureDetail } from "./errors.js";
import type { BridgeOutcome } from "./outcome.js";

/**
 * `domain-failed` means the command failed with its declared failure type.
 * `timed-out` is a client-side terminal state: `wait({ timeout })` passed its
 * deadline and sent a cancellation request. .NET never reports it.
 */
export type BridgeOperationStatusKind =
  "running" | "succeeded" | "domain-failed" | "failed" | "cancelled" | "expired" | "unknown" | "timed-out";
/**
 * Why a completed operation's result or failure could not be delivered (specs/application, "Status"). Open: a
 * minor release may add a kind.
 */
export type BridgeOperationDeliveryFailureKind = "result-too-large" | "result-encoding-failed" | "stream-overflow" | "stream-retention-too-large";
/** Why a completed operation's result or failure could not be delivered. */
export interface BridgeOperationDeliveryFailure { readonly kind: BridgeOperationDeliveryFailureKind; readonly message: string }
interface BridgeOperationIdentity { readonly contract: string; readonly requestId: string }

/**
 * Every operation status, including `domain-failed`. Generic code that builds
 * statuses uses this type; applications read {@link BridgeOperationStatus}.
 */
export type BridgeOperationStatusOf<TResult, TFailure> =
  | BridgeOperationIdentity & {
    readonly kind: "running";
    /** How far the command got, from 0 to 1, when it reports progress; absent while the progress is unknown. */
    readonly progress?: number;
  }
  | BridgeOperationIdentity & {
    readonly kind: "succeeded";
    readonly result?: TResult;
    /** The result could not be retained or delivered. */
    readonly delivery?: BridgeOperationDeliveryFailure;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "domain-failed";
    /** The declared failure; absent when it could not be delivered (`delivery`). */
    readonly failure?: TFailure;
    readonly delivery?: BridgeOperationDeliveryFailure;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "failed";
    /** Why the operation failed. `detail` is present only when the producer runs in development. */
    readonly error: { readonly kind: "failed"; readonly message: string; readonly detail?: BridgeFailureDetail };
    readonly stream?: true;
  }
  | BridgeOperationIdentity & { readonly kind: "cancelled" | "expired" | "unknown"; readonly stream?: true }
  | BridgeOperationIdentity & {
    readonly kind: "timed-out";
    /** How .NET answered the cancellation request, or `unobserved` when the request failed. */
    readonly cancellation: BridgeOperationCancelKind | "unobserved";
  };

/**
 * The status of an operation, discriminated by `kind`. An operation that
 * declares no failure type never reports `domain-failed`.
 */
export type BridgeOperationStatus<TResult = void, TFailure = never> =
  [TFailure] extends [never]
    ? Exclude<BridgeOperationStatusOf<TResult, never>, { readonly kind: "domain-failed" }>
    : BridgeOperationStatusOf<TResult, TFailure>;
export type BridgeOperationCancelKind = "cancellation-requested" | "not-running" | "unknown" | "expired";
/** Options of a generated `start{Command}()` call. */
export interface BridgeOperationStartOptions {
  /**
   * Names the operation, so the page can find it again with `recover{Command}(requestId)`, for example
   * after a reload. The default is a random UUID. A start with a request id that .NET already admitted
   * returns that operation instead of starting another.
   */
  readonly requestId?: string;
}
export interface BridgeOperationWaitOptions {
  /**
   * Milliseconds to wait for the terminal status. When it passes, the client
   * sends a cancellation request and resolves to a `timed-out` status.
   */
  readonly timeout?: number;
  /**
   * Abandons this wait when it aborts: the call rejects with `signal.reason`, an
   * `AbortError` DOMException unless `abort(reason)` gave another one. Unlike
   * `timeout`, it sends no cancellation request; the operation keeps running and
   * other waits are unaffected. Call `cancel()` to stop it. An already aborted
   * signal rejects at once; the wait removes its abort listener when it settles.
   */
  readonly signal?: AbortSignal | undefined;
}
export interface BridgeOperationCancelResult { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationCancelKind; }
export interface BridgeOperationStreamItem<TResult> { readonly sequence: number; readonly value: TResult; }
/** One page of a stream operation's items. */
export interface BridgeOperationStreamPage<TResult> {
  readonly contract: string;
  readonly requestId: string;
  /** The operation's status kind. A page is never `timed-out`, which only `wait()` decides. */
  readonly kind: Exclude<BridgeOperationStatusKind, "timed-out">;
  /** The cursor of the next page: pass it to the next `stream(cursor)` call. */
  readonly cursor?: number;
  readonly completed?: boolean;
  readonly items?: readonly BridgeOperationStreamItem<TResult>[];
  readonly delivery?: BridgeOperationDeliveryFailure;
}

/**
 * A recoverable command execution identified by its request id (the protocol's operation id). Its statuses carry
 * `contract`, the View's operation contract `{view}:{fingerprint}`, and `requestId`.
 */
export interface BridgeOperation<TResult = void, TFailure = never> {
  readonly requestId: string;
  /** Reads the current status. A terminal status resolves once the View's state includes the operation's effects. */
  status(): Promise<BridgeOperationStatus<TResult, TFailure>>;
  /**
   * Calls `listener` with each status the client observes, such as `running` with a new `progress` and the terminal
   * status, until the returned function is called. A subscription watches the operation on the producer, which
   * pushes its changes; the watch ends with the terminal status or when the last observer leaves. A terminal status
   * is delivered once the View's state includes the operation's effects. A listener that throws is reported and does
   * not stop delivery to others.
   */
  subscribe(listener: (status: BridgeOperationStatus<TResult, TFailure>) => void): () => void;
  /** The terminal status. A failed observation is retried by the next read. */
  readonly completion: Promise<BridgeOperationStatus<TResult, TFailure>>;
  /**
   * Waits for the terminal status, which the producer pushes to a `watch`. It resolves once the View's state
   * includes the operation's effects. With `timeout`, resolves to a `timed-out` status after cancelling the
   * operation, unless it finished first. With `signal`, rejects with its abort reason when it aborts first.
   */
  wait(options?: BridgeOperationWaitOptions): Promise<BridgeOperationStatus<TResult, TFailure>>;
  cancel(): Promise<BridgeOperationCancelResult>;
  /**
   * Waits like `wait()` and resolves to the result, or to the declared failure.
   * Rejects with `BridgeError` for `failed` (`failed`), `cancelled` (`cancelled`),
   * `timed-out` (`timeout`) and a result or failure that could not be delivered
   * (`failed`), and with `BridgeOperationUncertainError` for `expired` and
   * `unknown`: the outcome is unknown, so do not retry blindly. Rejects with the
   * abort reason when `signal` aborts first.
   */
  outcome(options?: BridgeOperationWaitOptions): Promise<BridgeOutcome<TResult, TFailure>>;
}

/**
 * An operation whose command yields a stream of `TItem` values, read through
 * `stream()`. Its status and `outcome()` carry no value, so it is a
 * `BridgeOperation<void, TFailure>`.
 */
export interface BridgeStreamOperation<TItem = void, TFailure = never> extends BridgeOperation<void, TFailure> {
  stream(cursor?: number): Promise<BridgeOperationStreamPage<TItem>>;
}

/** What the runtime returns for a started or recovered operation: either handle shape. */
export type BridgeOperationRuntimeHandle<TResult, TFailure> = BridgeOperation<TResult, TFailure> & BridgeStreamOperation<TResult, TFailure>;

/** How generated code decodes the values of one operation. Generated code is its only intended caller. */
export interface BridgeOperationCodec<TResult, TFailure = never> {
  /** Decodes the command's result, or a stream item for a stream operation. */
  readonly decode: (value: unknown) => TResult;
  /** Decodes the declared failure; given exactly when the command declares a failure type. */
  readonly decodeFailure?: ((value: unknown) => TFailure) | undefined;
  /** The command yields a stream, read with `stream()`. */
  readonly stream?: boolean | undefined;
}

/** The connection an operation starts on. Created by `connectView`; its members are internal to the runtime. */
export interface OperationScope {
  /** The View route. */
  readonly route: string;
  /** The View's operation contract, `{view}:{fingerprint}`. */
  readonly contract: string;
}

/**
 * The operation protocol, passed to `connectView` as `operations` by a generated client with operations. A View
 * without operations does not bundle it.
 */
export interface BridgeOperations {
  /** Sends `start` and returns the admitted operation. `input` is the encoded input, or undefined for none. */
  start<TResult, TFailure = never>(scope: OperationScope, member: string, operation: string, input: unknown,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  /** Reads the status of a known operation id and returns its handle. */
  recover<TResult, TFailure = never>(scope: OperationScope, member: string, operation: string,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

export const bridgeOperations: BridgeOperations = {
  start(scope, _member, operation) {
    return Promise.reject(uncertainOperation(scope.contract, scope.route, operation, "Not implemented yet.", new BridgeError("failed", "stub")));
  },
  recover(scope, _member, operation) {
    return Promise.reject(uncertainOperation(scope.contract, scope.route, operation, "Not implemented yet."));
  },
};
