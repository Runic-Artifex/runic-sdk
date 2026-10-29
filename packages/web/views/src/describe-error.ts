import { BridgeError, BridgeOperationUncertainError, type BridgeErrorKind } from "./errors.js";

// The brand of `FieldError` (field-controller.ts), read here so that describing an error does not bundle the field
// controller.
const fieldErrorBrand = Symbol.for("runic.views.FieldError");

/**
 * True when `error` is a {@link BridgeError}, of `kind` or one of the kinds when given. Narrows `error.kind`, so
 * `if (isBridgeError(error, "released")) …` needs no cast. Holds for an error of another copy of this package too.
 */
export function isBridgeError<TKind extends BridgeErrorKind = BridgeErrorKind>(error: unknown,
  kind?: TKind | readonly TKind[]): error is BridgeError & { readonly kind: TKind } {
  if (!(error instanceof BridgeError)) return false;
  if (kind === undefined) return true;
  return typeof kind === "string" ? error.kind === kind : (kind as readonly BridgeErrorKind[]).includes(error.kind);
}

const kindText: Readonly<Record<BridgeErrorKind, string>> = {
  unavailable: "This page is not running in a Runic host, so it cannot reach the application.",
  timeout: "The application did not answer in time.",
  disconnected: "The connection to the application was lost. It is restored automatically.",
  released: "This view was closed or replaced.",
  cancelled: "The request was cancelled.",
  rejected: "The application rejected the request.",
  failed: "The request failed.",
};

function message(error: Error): string | undefined {
  const text = error.message.trim();
  return text.length === 0 ? undefined : text;
}

/**
 * One English sentence that tells a user what went wrong, for a status line or an alert, instead of
 * `String(error)` ("BridgeError: …"). Accepts anything a binding reports as `error`:
 *
 * - a {@link BridgeError}: by kind and reason. A `rejected` or `failed` error shows the producer's message, such as
 *   a setter's validation text, when it has one; the others name the condition (`disconnected`, `released`, …).
 * - a `FieldError` (a checked write's receipt, or text that does not parse): its message.
 * - a `BridgeOperationUncertainError`: that the operation's outcome is unknown.
 * - another `Error`: its message; anything else: its text. `undefined` and `null` give `""`.
 *
 * Development detail (`error.detail`) is never included; the DevTools timeline shows it.
 */
export function describeBridgeError(error: unknown): string {
  if (error === undefined || error === null) return "";
  if (error instanceof BridgeOperationUncertainError)
    return "It is not known whether the operation finished. Check its result before you try again.";
  if (error instanceof BridgeError) {
    if (error.reason === "state-withheld") return "The change was made, but the page cannot show it yet.";
    if (error.reason === "protocol-mismatch") return "This page and the application use different Runic versions. Rebuild the frontend.";
    if (error.reason === "unavailable") return message(error) ?? "This action is not available right now.";
    if (error.reason === "capacity") return "The application is busy. Try again in a moment.";
    if (error.kind === "rejected" || error.kind === "failed") return message(error) ?? kindText[error.kind];
    return kindText[error.kind] ?? message(error) ?? "The request failed.";
  }
  if (typeof error === "object" && (error as Record<symbol, unknown>)[fieldErrorBrand] === true)
    return message(error as Error) ?? "The value was not saved.";
  if (error instanceof Error) return message(error) ?? error.name;
  return String(error);
}
