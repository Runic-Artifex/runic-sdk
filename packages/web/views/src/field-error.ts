import type { FieldBaseline } from "./connection.js";

/**
 * Why a field's write or text did not take effect (`FieldState.error`):
 *
 * - `rejected`: the checked write's setter refused the value; the field is unchanged and the draft is kept;
 * - `conflict`: another writer changed the field since the draft started; `current` is its value and version, the
 *   draft is kept and nothing is written until `retry()` or `reset()`;
 * - `committed-with-error`: the value was committed, then the setter or its validation failed; `current` is the
 *   committed field;
 * - `invalid-text`: the text typed into the field is not a value of its kind, such as `1e` for a number; nothing is
 *   written.
 *
 * Open: a later release may add kinds.
 */
export type FieldErrorKind = "rejected" | "conflict" | "committed-with-error" | "invalid-text";

const fieldErrorBrand = Symbol.for("runic.views.FieldError");

/** A checked write's receipt that is not a plain success, or a text that does not parse. See {@link FieldErrorKind}. */
export class FieldError extends Error {
  /** The producer's field after the write: the incoming value of a `conflict`, the committed one of `committed-with-error`. */
  readonly current?: FieldBaseline<unknown>;

  constructor(readonly kind: FieldErrorKind, message: string, current?: FieldBaseline<unknown>) {
    super(message);
    this.name = "FieldError";
    if (current !== undefined) this.current = current;
    Object.defineProperty(this, fieldErrorBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === FieldError
      ? typeof value === "object" && value !== null && (value as Record<symbol, unknown>)[fieldErrorBrand] === true
      : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}
