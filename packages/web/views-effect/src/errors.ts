import { BridgeError, BridgeOperationUncertainError, type BridgeFailureDetail, type BridgeOperationCancelKind,
  type BridgeOperationStatus } from "@runic-artifex/views";
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
/** The client was disposed, the Bridge session changed, or the transport is closed (`disconnected`). */
export class ViewDisconnected extends Data.TaggedError("ViewDisconnected")<ViewFailureFields> {}
/** A Bridge exists but did not connect in time (`timeout`). */
export class ViewBridgeTimeout extends Data.TaggedError("ViewBridgeTimeout")<ViewFailureFields> {}
/** .NET refused the call, for example a command whose `CanExecute` is false (`rejected`). */
export class ViewRejected extends Data.TaggedError("ViewRejected")<ViewFailureFields> {}
/** .NET cancelled the call (`cancelled`). */
export class ViewCancelled extends Data.TaggedError("ViewCancelled")<ViewFailureFields> {}
/**
 * The .NET handler threw, or the reply was invalid (`failed`). `detail` holds the
 * exception type, message and stack when .NET runs in development.
 */
export class ViewCommandFailed extends Data.TaggedError("ViewCommandFailed")<ViewFailureFields & {
  readonly detail: BridgeFailureDetail | undefined;
}> {}

/** The failures of a command, setter, query or connection. */
export type ViewError = ViewUnavailable | ViewDisconnected | ViewBridgeTimeout | ViewRejected | ViewCancelled | ViewCommandFailed;

/**
 * The client could not observe whether .NET admitted or completed an operation,
 * or .NET no longer knows its outcome (`expired` or `unknown`). Starting it again
 * with the same request ID returns the existing operation instead of a second one.
 */
export class ViewOperationUncertain extends Data.TaggedError("ViewOperationUncertain")<{
  readonly message: string;
  readonly requestId: string;
  readonly cause: unknown;
}> {}
/** The operation ended as `failed`, or succeeded without a deliverable result. */
export class ViewOperationFailed extends Data.TaggedError("ViewOperationFailed")<{
  readonly message: string;
  readonly requestId: string;
  readonly status: BridgeOperationStatus<unknown>;
  readonly detail: BridgeFailureDetail | undefined;
}> {}
/** The operation ended as `cancelled`, by this client or another one. */
export class ViewOperationCancelled extends Data.TaggedError("ViewOperationCancelled")<{
  readonly message: string;
  readonly requestId: string;
  readonly status: BridgeOperationStatus<unknown>;
}> {}
/** The `timeout` option passed and the operation was cancelled. */
export class ViewOperationTimedOut extends Data.TaggedError("ViewOperationTimedOut")<{
  readonly message: string;
  readonly requestId: string;
  readonly timeout: Duration.Duration;
  /** How .NET answered the cancellation request, or `unobserved` when the request failed. */
  readonly cancellation: BridgeOperationCancelKind | "unobserved";
}> {}

/** The failures of an operation: starting it, observing it, or its terminal status. */
export type ViewOperationError =
  | ViewError | ViewOperationUncertain | ViewOperationFailed | ViewOperationCancelled | ViewOperationTimedOut;

/** Maps a {@link BridgeError} to its tagged error. An unknown future kind maps to `ViewCommandFailed`. */
export function fromBridgeError(error: BridgeError): ViewError {
  const fields: ViewFailureFields = { message: error.message, route: error.route, cause: error };
  switch (error.kind) {
    case "unavailable": return new ViewUnavailable(fields);
    case "disconnected": return new ViewDisconnected(fields);
    case "timeout": return new ViewBridgeTimeout(fields);
    case "rejected": return new ViewRejected(fields);
    case "cancelled": return new ViewCancelled(fields);
    default: return new ViewCommandFailed({ ...fields, detail: error.detail });
  }
}

/** Maps a rejection of a client method. Anything that is not a Bridge failure, such as a `RangeError` for an invalid argument, is a defect. */
export function bridgeFailure(cause: unknown): ViewError | ViewOperationUncertain | undefined {
  if (cause instanceof BridgeError) return fromBridgeError(cause);
  if (cause instanceof BridgeOperationUncertainError)
    return new ViewOperationUncertain({ message: cause.message, requestId: cause.requestId, cause });
  return undefined;
}
