// The type-level brand. At run time the brand is the registered symbol
// `Symbol.for("runic.bridgeOutcome")` as a non-enumerable property, so every
// copy of this package recognizes an outcome, while a spread or a JSON
// round-trip produces a plain object that is not one.
declare const outcomeBrand: unique symbol;
const runtimeBrand = Symbol.for("runic.bridgeOutcome");

/**
 * How a command or operation that declares a failure type ended: `ok` with its
 * value, or not `ok` with the declared `failure`. Unexpected failures still
 * reject with `BridgeError`.
 */
export type BridgeOutcome<TValue, TFailure> =
  | { readonly ok: true; readonly value: TValue; readonly [outcomeBrand]: true }
  | { readonly ok: false; readonly failure: TFailure; readonly [outcomeBrand]: true };

function branded<T extends object>(value: T): T {
  Object.defineProperty(value, runtimeBrand, { value: true });
  return Object.freeze(value);
}

/** A successful outcome with `value`. */
export function bridgeSuccess<TValue>(value: TValue): BridgeOutcome<TValue, never> {
  return branded({ ok: true, value }) as unknown as BridgeOutcome<TValue, never>;
}

/** An outcome with the declared `failure`. */
export function bridgeFailure<TFailure>(failure: TFailure): BridgeOutcome<never, TFailure> {
  return branded({ ok: false, failure }) as unknown as BridgeOutcome<never, TFailure>;
}

/** True for an outcome created by `bridgeSuccess` or `bridgeFailure`, including by another copy of this package. */
export function isBridgeOutcome(value: unknown): value is BridgeOutcome<unknown, unknown> {
  return typeof value === "object" && value !== null && (value as Record<symbol, unknown>)[runtimeBrand] === true;
}

/**
 * Handles every case of a `$case` union, such as a declared failure:
 *
 * ```ts
 * const message = matchCase(outcome.failure, {
 *   titleRequired: () => "A note needs a title.",
 *   titleTaken: failure => `"${failure.existingTitle}" exists.`,
 * });
 * ```
 *
 * A missing handler is a type error. The result is the union of the handlers' return types.
 */
export function matchCase<
  F extends { readonly $case: string },
  H extends { readonly [K in F["$case"]]: (value: Extract<F, { readonly $case: K }>) => unknown },
>(value: F, cases: H): ReturnType<H[F["$case"]]> {
  const handler = (cases as unknown as Record<string, ((value: F) => unknown) | undefined>)[value.$case];
  if (typeof handler !== "function" || !Object.hasOwn(cases, value.$case))
    throw new TypeError(`No handler for the case "${String(value.$case)}".`);
  return handler(value) as ReturnType<H[F["$case"]]>;
}
