import { BridgeError, BridgeOperationUncertainError, type BridgeFailureDetail, type BridgeOperationCancelKind,
  type BridgeOperationStatusOf } from "@runic-artifex/views";
import * as Data from "effect/Data";
import * as Duration from "effect/Duration";

/** Fields shared by the errors that map one {@link BridgeError} kind. */
export interface ViewFailureFields {
  readonly message: string;
  /** The Bridge route that failed, when known. */
  readonly route: string | undefined;
  /** The original error from `@runic-artifex/views`. */
  readonly cause: BridgeError;
}

/** No host installed `window.__runicBridge`, for example a page opened from a plain Vite server (`unavailable`). */
export class ViewUnavailable extends Data.TaggedError("ViewUnavailable")<ViewFailureFields> {}
/**
 * The Bridge is not connected to .NET: the transport closed or the host ended (`disconnected`).
 * The client decides this; .NET never sends it.
 */
export class ViewDisconnected extends Data.TaggedError("ViewDisconnected")<ViewFailureFields> {}
/**
 * The Bridge is connected, but this View was released: the client was disposed, or .NET
 * released the View, for example because navigation replaced it (`released`).
 */
export class ViewReleased extends Data.TaggedError("ViewReleased")<ViewFailureFields> {}
/** A Bridge exists but did not connect in time, or a call did not complete in time (`timeout`). */
export class ViewTimeout extends Data.TaggedError("ViewTimeout")<ViewFailureFields> {}
/**
 * .NET refused the call, for example a command whose `CanExecute` is false (`rejected`).
 * `cause.reason` refines it when .NET sent one, such as `unavailable` or `invalid-input`.
 */
export class ViewRejected extends Data.TaggedError("ViewRejected")<ViewFailureFields> {}
/** .NET cancelled the call (`cancelled`). */
export class ViewCancelled extends Data.TaggedError("ViewCancelled")<ViewFailureFields> {}
/**
 * A connect, command, setter or query failed: the .NET handler threw, the reply was
 * invalid, or the .NET host speaks another Views protocol version (`failed`). `detail` holds the exception type, message and stack when .NET runs
 * in development.
 */
export class ViewFailed extends Data.TaggedError("ViewFailed")<ViewFailureFields & {
  readonly detail: BridgeFailureDetail | undefined;
}> {}

/** The failures of a command, setter, query or connection. */
export type ViewError =
  | ViewUnavailable | ViewDisconnected | ViewReleased | ViewTimeout | ViewRejected | ViewCancelled | ViewFailed;

/**
 * The client could not observe whether .NET admitted or completed an operation,
 * or .NET no longer knows its outcome (`expired` or `unknown`). Starting it again
 * with the same request ID returns the existing operation instead of a second one.
 */
export class ViewOperationUncertain extends Data.TaggedError("ViewOperationUncertain")<{
  readonly message: string;
  readonly requestId: string;
  /** The terminal status (`expired`, `unknown`) or last observed status, when the outcome came from one. */
  readonly status: BridgeOperationStatusOf<unknown, unknown> | undefined;
  readonly cause: unknown;
}> {}
/** The operation ended as `failed`, or succeeded without a deliverable result. */
export class ViewOperationFailed extends Data.TaggedError("ViewOperationFailed")<{
  readonly message: string;
  readonly requestId: string;
  readonly status: BridgeOperationStatusOf<unknown, unknown>;
  readonly detail: BridgeFailureDetail | undefined;
}> {}
/** The operation ended as `cancelled`, by this client or another one. */
export class ViewOperationCancelled extends Data.TaggedError("ViewOperationCancelled")<{
  readonly message: string;
  readonly requestId: string;
  readonly status: BridgeOperationStatusOf<unknown, unknown>;
}> {}
/**
 * The operation did not complete in time: the `timeout` option of {@link operation} passed and the
 * operation was cancelled, or the plain client's `wait({ timeout })` resolved to its client-side
 * `timed-out` status (.NET never reports one).
 */
export class ViewOperationTimedOut extends Data.TaggedError("ViewOperationTimedOut")<{
  readonly message: string;
  readonly requestId: string;
  /** The `timeout` option that passed, when this adapter applied it. */
  readonly timeout: Duration.Duration | undefined;
  /** How .NET answered the cancellation request, or `unobserved` when the request failed. */
  readonly cancellation: BridgeOperationCancelKind | "unobserved";
}> {}

/**
 * The declared failure of a command or operation (`[RunicFailure]` in .NET):
 * `failure` is the value the generated client decoded, such as
 * `{ $case: "titleRequired" }`. Handle it with `Effect.catchTag("ViewDomainFailure", ...)`
 * or, for a `$case` union, with {@link catchCase}.
 */
export class ViewDomainFailure<F = unknown> extends Data.TaggedError("ViewDomainFailure")<{
  readonly message: string;
  readonly failure: unknown;
  /** The operation's request id, for a failure that ended an operation. */
  readonly requestId: string | undefined;
}> {
  declare readonly failure: F;

  constructor(args: { readonly message: string; readonly failure: F; readonly requestId: string | undefined }) {
    super(args);
  }
}

/** The {@link ViewDomainFailure} a command or operation with failure type `F` adds; nothing when `F` is `never`. */
export type ViewDomainFailureOf<F> = [F] extends [never] ? never : ViewDomainFailure<F>;

/** The failures of an operation: starting it, observing it, or its terminal status. */
export type ViewOperationError =
  | ViewError | ViewOperationUncertain | ViewOperationFailed | ViewOperationCancelled | ViewOperationTimedOut;

/** Maps a {@link BridgeError} to its tagged error. An unknown future kind maps to `ViewFailed`. */
export function fromBridgeError(error: BridgeError): ViewError {
  const fields: ViewFailureFields = { message: error.message, route: error.route, cause: error };
  switch (error.kind) {
    case "unavailable": return new ViewUnavailable(fields);
    case "disconnected": return new ViewDisconnected(fields);
    case "released": return new ViewReleased(fields);
    case "timeout": return new ViewTimeout(fields);
    case "rejected": return new ViewRejected(fields);
    case "cancelled": return new ViewCancelled(fields);
    case "failed": return new ViewFailed({ ...fields, detail: error.detail });
    default: {
      // A kind added by a newer `@runic-artifex/views` is a failure until this adapter maps it.
      const _unmapped: never = error.kind;
      return new ViewFailed({ ...fields, detail: error.detail });
    }
  }
}

/** Maps a rejection of a client method. Anything that is not a Bridge failure, such as a `RangeError` for an invalid argument, is a defect. */
export function bridgeFailure(cause: unknown): ViewError | ViewOperationUncertain | undefined {
  if (cause instanceof BridgeError) return fromBridgeError(cause);
  if (cause instanceof BridgeOperationUncertainError)
    return new ViewOperationUncertain({ message: cause.message, requestId: cause.requestId, status: undefined, cause });
  return undefined;
}
