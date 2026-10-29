/**
 * Why a Bridge call did not complete. `unavailable` means no host installed
 * `window.__runicBridge`; `timeout` means a Bridge exists but did not connect in time.
 * `disconnected` means the Bridge itself is not connected to .NET (the client decides
 * this; .NET never sends it); `released` means the Bridge is connected but .NET or the
 * page released this View, for example because navigation replaced it.
 */
export type BridgeErrorKind = "rejected" | "cancelled" | "failed" | "disconnected" | "released" | "timeout" | "unavailable";

/**
 * Refines a {@link BridgeError} kind (specs/application, "Errors"). With `rejected`: `unavailable` (a command that
 * is not available), `invalid-input` (a value or input that does not decode or that the setter or command rejects,
 * or a request over the input limit), `unresolved-key` (the key of a key command names no object), `capacity` (a
 * limit of the window or connection), `invalid-request` and `no-hello`. With `failed`: `protocol-mismatch` (the host
 * speaks another Views protocol). With any kind: `state-withheld` (the call ran, but no state that includes its
 * effects can be published; do not retry it as if it had not run). An operation start rejected for a reason
 * carries it too, such as `identity-conflict`. Open: a minor release may add a reason, and the client keeps a reason
 * it does not know, so handle unknown reasons.
 */
export type BridgeErrorReason =
  | "unavailable" | "invalid-input" | "unresolved-key" | "capacity" | "invalid-request" | "no-hello" | "protocol-mismatch"
  | "state-withheld" | "splice-base" | (string & {});

/**
 * Failure detail that a producer adds to an error reply only in development (.NET: host environment
 * `Development`, or the window option `IncludeFailureDetail`).
 */
export interface BridgeFailureDetail {
  /** The producer's name for the error's type, if it has one, such as `System.InvalidOperationException`. */
  readonly type?: string;
  readonly message: string;
  /** Text that describes where the error came from, such as a stack trace and its causes. */
  readonly stack?: string;
}

// A producer adds `detail` to an error reply only in development. A malformed
// detail is dropped rather than failing the call. Not exported from the package.
export function decodeFailureDetail(value: unknown): BridgeFailureDetail | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return undefined;
  const { type, message, stack } = value as Record<string, unknown>;
  if (typeof message !== "string") return undefined;
  return { ...(typeof type === "string" ? { type } : {}), message, ...(typeof stack === "string" ? { stack } : {}) };
}

export interface BridgeErrorOptions {
  /** The underlying error, kept as `error.cause`. */
  readonly cause?: unknown;
  /** The View route of the request that failed, such as `counter`, or `runic.menu-bar`. */
  readonly route?: string;
  readonly detail?: BridgeFailureDetail;
  /** What refines `kind`, such as `unavailable` for a `rejected` command. */
  readonly reason?: BridgeErrorReason;
}

// Brands are registered symbols, so `instanceof` also holds for an error
// created by another copy of this package on the same page.
const bridgeErrorBrand = Symbol.for("runic.views.BridgeError");
const uncertainBrand = Symbol.for("runic.views.BridgeOperationUncertainError");

function branded(value: unknown, brand: symbol): boolean {
  return typeof value === "object" && value !== null && (value as Record<symbol, unknown>)[brand] === true;
}

/** A Bridge call that .NET or the transport did not complete. */
export class BridgeError extends Error {
  /** The View route of the request that failed, when known. */
  readonly route?: string;
  /** Development-only failure detail, when the producer sent it. */
  readonly detail?: BridgeFailureDetail;
  /** What refines `kind`, such as `unavailable` or `invalid-input` for `rejected`. */
  readonly reason?: BridgeErrorReason;

  constructor(readonly kind: BridgeErrorKind, message: string, options: BridgeErrorOptions = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "BridgeError";
    if (options.route !== undefined) this.route = options.route;
    if (options.detail !== undefined) this.detail = options.detail;
    if (options.reason !== undefined) this.reason = options.reason;
    Object.defineProperty(this, bridgeErrorBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeError ? branded(value, bridgeErrorBrand) : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}

/** The identity of an operation whose outcome the client could not observe. */
export interface BridgeOperationUncertainErrorOptions {
  /** The operation contract of the View, `{view}:{fingerprint}`, such as `Notes.EditorViewModel:7E66…`. */
  readonly contract: string;
  /** The operation's request id. */
  readonly requestId: string;
  /** The View route the operation runs on, such as `editor` or `content12`, when known. */
  readonly route?: string;
  /** The underlying error or status, kept as `error.cause`. */
  readonly cause?: unknown;
}

/**
 * The client could not observe whether .NET admitted or completed an operation, so its outcome is
 * unknown: do not retry it blindly. It is deliberately not a `BridgeError`, whose kinds are failures
 * a caller may retry.
 */
export class BridgeOperationUncertainError extends Error {
  /** The operation contract of the View, `{view}:{fingerprint}`. */
  readonly contract: string;
  /** The operation's request id. */
  readonly requestId: string;
  /** The View route the operation runs on, when known. */
  readonly route?: string;

  constructor(message: string, options: BridgeOperationUncertainErrorOptions) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "BridgeOperationUncertainError";
    this.contract = options.contract;
    this.requestId = options.requestId;
    if (options.route !== undefined) this.route = options.route;
    Object.defineProperty(this, uncertainBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeOperationUncertainError
      ? branded(value, uncertainBrand)
      : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}

export function uncertainOperation(contract: string, route: string | undefined, requestId: string, message: string,
  cause?: unknown): BridgeOperationUncertainError {
  return new BridgeOperationUncertainError(message, {
    contract, requestId, ...(route === undefined ? {} : { route }), ...(cause === undefined ? {} : { cause }),
  });
}
