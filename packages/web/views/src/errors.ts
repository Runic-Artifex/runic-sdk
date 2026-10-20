/**
 * Why a Bridge call did not complete. `unavailable` means no host installed
 * `window.__runicBridge`; `timeout` means a Bridge exists but did not connect in time.
 * `disconnected` means the Bridge itself is not connected to .NET; `released` means
 * the Bridge is connected but .NET or the page released this View, for example
 * because navigation replaced it.
 */
export type BridgeErrorKind = "rejected" | "cancelled" | "failed" | "disconnected" | "released" | "timeout" | "unavailable";

/**
 * Local failure detail that .NET adds to an error reply only in development
 * (host environment `Development`, or the window option `IncludeFailureDetail`).
 */
export interface BridgeFailureDetail {
  /** The exception type, such as `System.InvalidOperationException`. */
  readonly type: string;
  readonly message: string;
  /** The exception text with its stack trace and inner exceptions. */
  readonly stack?: string;
}

// .NET adds `detail` to an error reply only in development. A malformed
// detail is dropped rather than failing the call. Not exported from the package.
export function decodeFailureDetail(value: unknown): BridgeFailureDetail | undefined {
  if (value === null || typeof value !== "object") return undefined;
  const { type, message, stack } = value as Record<string, unknown>;
  if (typeof type !== "string" || typeof message !== "string") return undefined;
  return typeof stack === "string" ? { type, message, stack } : { type, message };
}

export interface BridgeErrorOptions {
  /** The underlying error, kept as `error.cause`. */
  readonly cause?: unknown;
  /** The Bridge route that failed, such as `counterIncrement`. */
  readonly route?: string;
  readonly detail?: BridgeFailureDetail;
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
  /** The Bridge route that failed, when known. */
  readonly route?: string;
  /** Development-only .NET failure detail, when the host sent it. */
  readonly detail?: BridgeFailureDetail;

  constructor(readonly kind: BridgeErrorKind, message: string, options: BridgeErrorOptions = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "BridgeError";
    if (options.route !== undefined) this.route = options.route;
    if (options.detail !== undefined) this.detail = options.detail;
    Object.defineProperty(this, bridgeErrorBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeError ? branded(value, bridgeErrorBrand) : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}

/** The identity of an operation whose outcome the client could not observe. */
export interface BridgeOperationUncertainErrorOptions {
  /** The generated Bridge contract of the View, such as `Notes.EditorViewModel:7E66…`. */
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
  /** The generated Bridge contract of the View. */
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

// Operations name their View as `{contract}:{route}` on the wire; the error keeps the two apart.
export function uncertainOperation(operationContract: string, requestId: string, message: string, cause?: unknown): BridgeOperationUncertainError {
  const separator = operationContract.lastIndexOf(":");
  const contract = separator < 0 ? operationContract : operationContract.slice(0, separator);
  const route = separator < 0 ? undefined : operationContract.slice(separator + 1);
  return new BridgeOperationUncertainError(message, {
    contract, requestId, ...(route === undefined ? {} : { route }), ...(cause === undefined ? {} : { cause }),
  });
}
