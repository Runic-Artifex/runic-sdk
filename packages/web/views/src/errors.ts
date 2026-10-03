export type BridgeErrorKind = "rejected" | "cancelled" | "failed" | "disconnected" | "timeout";

// Brands are registered symbols, so `instanceof` also holds for an error
// created by another copy of this package on the same page.
const bridgeErrorBrand = Symbol.for("runic.views.BridgeError");
const uncertainBrand = Symbol.for("runic.views.BridgeOperationUncertainError");

function branded(value: unknown, brand: symbol): boolean {
  return typeof value === "object" && value !== null && (value as Record<symbol, unknown>)[brand] === true;
}

/** A Bridge call that .NET or the transport did not complete. */
export class BridgeError extends Error {
  constructor(readonly kind: BridgeErrorKind, message: string) {
    super(message);
    this.name = "BridgeError";
    Object.defineProperty(this, bridgeErrorBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeError ? branded(value, bridgeErrorBrand) : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}

/** The client could not observe whether .NET admitted or completed an operation. */
export class BridgeOperationUncertainError extends Error {
  constructor(readonly contract: string, readonly requestId: string, message: string) {
    super(message);
    this.name = "BridgeOperationUncertainError";
    Object.defineProperty(this, uncertainBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === BridgeOperationUncertainError
      ? branded(value, uncertainBrand)
      : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}
