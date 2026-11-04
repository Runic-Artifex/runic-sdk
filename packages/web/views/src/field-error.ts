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
 *   written. Its {@link FieldError.reason} says why.
 *
 * Open: a later release may add kinds.
 */
export type FieldErrorKind = "rejected" | "conflict" | "committed-with-error" | "invalid-text";

/**
 * Why a text does not parse (`invalid-text`), for an application that shows its own text: the error's
 * {@link FieldError.arguments} carry the parts of the message, and `message` stays the English text.
 *
 * | Reason | English message | Arguments |
 * | --- | --- | --- |
 * | `required` | `Enter a number.` | `expected` |
 * | `not-a-number` | `1e is not a number.` | `text` |
 * | `not-a-whole-number` | `1.5 is not a whole number.` | `text` |
 * | `too-many-fraction-digits` | `1.234 has more than 2 digits after the decimal separator.` | `text`, `scale` |
 * | `out-of-range` | `…9 is too large.` (a `decimal` of 2⁹⁶ or more) | `text` |
 * | `not-a-boolean` | `yes is not true or false.` | `text` |
 * | `not-a-choice` | `Large is not one of Small, Medium.` | `text`, `choices` |
 * | `not-a-date` | `31.02.2026 is not a date.`, `x is not a date (DD.MM.YYYY or YYYY-MM-DD).` | `text`, `patterns`? |
 * | `not-a-time` | `25:00 is not a time.`, `x is not a time (hh:mm or hh:mm:ss).` | `text`, `patterns`? |
 * | `not-a-date-time` | `x is not a date and time (YYYY-MM-DDThh:mm).` | `text`, `patterns` |
 * | `offset-out-of-range` | `…+15:00 has an offset beyond ±14:00.` | `text` |
 * | `no-text-form` | `This field has no text form.` | none |
 *
 * `not-a-date` and `not-a-time` without `patterns` are a text of the right form that names no day or time, such as
 * 31 February. Open: a later release may add reasons; show `message` for one you do not know.
 */
export type FieldErrorReason =
  | "required" | "not-a-number" | "not-a-whole-number" | "too-many-fraction-digits" | "out-of-range"
  | "not-a-boolean" | "not-a-choice" | "not-a-date" | "not-a-time" | "not-a-date-time" | "offset-out-of-range"
  | "no-text-form";

/** The value a `required` text names (`Enter a number.`, `Enter a date and time.`). Open. */
export type FieldErrorExpected = "number" | "whole-number" | "date" | "time" | "date-time";

/** The parts of an `invalid-text` message ({@link FieldErrorReason}); each reason sets the ones its row lists. */
export interface FieldErrorArguments {
  /** The text the user typed, trimmed (as typed for `not-a-boolean` and `not-a-choice`). */
  readonly text?: string;
  /** The most digits after the decimal separator (`too-many-fraction-digits`). */
  readonly scale?: number;
  /** The values a choice takes (`not-a-choice`). */
  readonly choices?: readonly string[];
  /**
   * The forms a date or time text takes, the window locale's first, such as `["DD.MM.YYYY", "YYYY-MM-DD"]` or
   * `["hh:mm", "hh:mm:ss"]`. The English message joins them with "or".
   */
  readonly patterns?: readonly string[];
  /** What an empty field needs (`required`). */
  readonly expected?: FieldErrorExpected;
}

/** The reason and arguments of a {@link FieldError}, for its constructor. */
export interface FieldErrorDetail {
  readonly reason?: FieldErrorReason;
  readonly arguments?: FieldErrorArguments;
}

const fieldErrorBrand = Symbol.for("runic.views.FieldError");
const noArguments: FieldErrorArguments = Object.freeze({});

/**
 * A checked write's receipt that is not a plain success, or a text that does not parse. See {@link FieldErrorKind}.
 * `message` is English. Show a translated text with `resolveText(error)` (`useText`, `runicText` in the framework
 * packages) after `configureFieldErrorText`, which reads {@link reason} and {@link arguments}.
 */
export class FieldError extends Error {
  /** The producer's field after the write: the incoming value of a `conflict`, the committed one of `committed-with-error`. */
  readonly current?: FieldBaseline<unknown>;
  /**
   * Why a text does not parse, for an `invalid-text` error of Runic's parsers; `undefined` for the other kinds and for
   * a text that a field's own `parse` refused.
   */
  readonly reason?: FieldErrorReason;
  /** The parts of the message for {@link reason}; empty without one. Frozen. */
  readonly arguments: FieldErrorArguments;

  constructor(readonly kind: FieldErrorKind, message: string, current?: FieldBaseline<unknown>, detail?: FieldErrorDetail) {
    super(message);
    this.name = "FieldError";
    if (current !== undefined) this.current = current;
    if (detail?.reason !== undefined) this.reason = detail.reason;
    const args = detail?.arguments;
    this.arguments = args === undefined ? noArguments
      : Object.freeze({ ...args, ...args.choices === undefined ? {} : { choices: Object.freeze([...args.choices]) },
        ...args.patterns === undefined ? {} : { patterns: Object.freeze([...args.patterns]) } });
    Object.defineProperty(this, fieldErrorBrand, { value: true });
  }

  static [Symbol.hasInstance](value: unknown): boolean {
    return this === FieldError
      ? isFieldError(value)
      : Function.prototype[Symbol.hasInstance].call(this, value);
  }
}

/** @internal A `FieldError` of any copy of this package. */
export function isFieldError(value: unknown): value is FieldError {
  return typeof value === "object" && value !== null && (value as Record<symbol, unknown>)[fieldErrorBrand] === true;
}

const expectedText: Readonly<Record<FieldErrorExpected, string>> = {
  "number": "a number", "whole-number": "a whole number", "date": "a date", "time": "a time", "date-time": "a date and time",
};

/** @internal The English message of an `invalid-text` reason, as Runic's parsers write it. */
export function fieldErrorMessage(reason: FieldErrorReason, args: FieldErrorArguments = noArguments): string {
  const text = args.text ?? "";
  const hint = args.patterns === undefined || args.patterns.length === 0 ? "" : ` (${args.patterns.join(" or ")})`;
  switch (reason) {
    case "required": return `Enter ${expectedText[args.expected ?? "number"] ?? "a value"}.`;
    case "not-a-number": return `${text} is not a number.`;
    case "not-a-whole-number": return `${text} is not a whole number.`;
    case "too-many-fraction-digits": return `${text} has more than ${args.scale} digits after the decimal separator.`;
    case "out-of-range": return `${text} is too large.`;
    case "not-a-boolean": return `${text} is not true or false.`;
    case "not-a-choice": return `${text} is not one of ${(args.choices ?? []).join(", ")}.`;
    case "not-a-date": return `${text} is not a date${hint}.`;
    case "not-a-time": return `${text} is not a time${hint}.`;
    case "not-a-date-time": return `${text} is not a date and time${hint}.`;
    case "offset-out-of-range": return `${text} has an offset beyond ±14:00.`;
    case "no-text-form": return "This field has no text form.";
    default: return `${text} is not valid.`;
  }
}

/** @internal An `invalid-text` error of Runic's parsers, with its English message. */
export function invalidText(reason: FieldErrorReason, args?: FieldErrorArguments): FieldError {
  return new FieldError("invalid-text", fieldErrorMessage(reason, args), undefined, { reason, ...args === undefined ? {} : { arguments: args } });
}
