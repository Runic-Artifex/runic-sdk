/**
 * Why a Bridge call did not complete. `unavailable` means no host installed
 * `window.__runicBridge`; `timeout` means a Bridge exists but did not connect in time.
 */
export type BridgeErrorKind = "rejected" | "cancelled" | "failed" | "disconnected" | "timeout" | "unavailable";

/**
 * Local failure detail that .NET adds to an error reply only in development
 * (host environment `Development`, or `BridgeDiagnostics.IncludeFailureDetail`).
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

/** The client could not observe whether .NET admitted or completed an operation. */
export class BridgeOperationUncertainError extends Error {
  constructor(readonly contract: string, readonly requestId: string, message: string, options: { readonly cause?: unknown } = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "BridgeOperationUncertainError";
    Object.defineProperty(this, uncertainBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeOperationUncertainError
      ? branded(value, uncertainBrand)
      : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}
