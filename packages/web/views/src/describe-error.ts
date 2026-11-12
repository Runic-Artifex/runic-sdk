import { BridgeError, BridgeOperationUncertainError, type BridgeErrorKind } from "./errors.js";
import { isFieldError } from "./field-error.js";
import type { RunicTextKey } from "./runic-text.js";
import { fieldErrorText, runicText } from "./translated-text.js";

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

const kindText: Readonly<Record<BridgeErrorKind, RunicTextKey>> = {
  unavailable: "bridge.unavailable",
  timeout: "bridge.timeout",
  disconnected: "bridge.disconnected",
  released: "bridge.released",
  cancelled: "bridge.cancelled",
  rejected: "bridge.rejected",
  failed: "bridge.failed",
};

// The reasons that say more than the kind. Another reason, also one this version does not know, gives the kind's text.
const reasonText: Readonly<Partial<Record<string, RunicTextKey>>> = {
  "state-withheld": "bridge.state_withheld",
  "protocol-mismatch": "bridge.protocol_mismatch",
  "unavailable": "bridge.action_unavailable",
  "capacity": "bridge.busy",
  "busy": "bridge.busy",
  "invalid-input": "bridge.invalid_input",
  "unresolved-key": "bridge.item_not_found",
};

function message(error: Error): string | undefined {
  const text = error.message.trim();
  return text.length === 0 ? undefined : text;
}

/**
 * One sentence that tells a user what went wrong, for a status line or an alert, instead of `String(error)`
 * ("BridgeError: …"), in the window's language when the page has Runic's text (`configureRunicText`, which
 * `configureRunicViewsText()` of `@runic-artifex/views-translations` sets). Accepts anything a binding reports as
 * `error`:
 *
 * - a {@link BridgeError}: Runic's sentence for its reason, such as `capacity` or `invalid-input`, or else for its kind
 *   (`disconnected`, `released`, `failed`, …). Its `message` is the producer's diagnostic text, such as
 *   `Count has an invalid value.`, and is not shown.
 * - a `FieldError` (a checked write's receipt, or text that does not parse): the application's text for it
 *   (`configureFieldErrorText`), or its message, which for a rejected write is the application's validation text.
 * - a `BridgeOperationUncertainError`: that the operation's outcome is unknown.
 * - another `Error`: its message; anything else: its text. `undefined` and `null` give `""`.
 *
 * Development detail (`error.detail`) is never included; the DevTools timeline shows it.
 */
export function describeBridgeError(error: unknown): string {
  if (error === undefined || error === null) return "";
  if (error instanceof BridgeOperationUncertainError) return runicText("bridge.outcome_unknown");
  if (error instanceof BridgeError) {
    const reason = typeof error.reason === "string" && Object.hasOwn(reasonText, error.reason) ? reasonText[error.reason] : undefined;
    return runicText(reason ?? kindText[error.kind] ?? "bridge.failed");
  }
  if (isFieldError(error)) {
    const text = fieldErrorText(error).trim();
    return text.length === 0 ? runicText("bridge.value_not_saved") : text;
  }
  if (error instanceof Error) return message(error) ?? error.name;
  return String(error);
}
