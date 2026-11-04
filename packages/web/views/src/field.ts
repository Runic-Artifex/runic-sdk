import type { FieldBaseline, FieldWriteOptions, FieldWriteReceipt } from "./connection.js";
import type { BridgeValidationState } from "./validation.js";
import { resolveText, textVersion } from "./translated-text.js";

/**
 * How a field's value is edited, which selects its text form and input wiring:
 *
 * - `text`: a TypeScript `string` (strings, `Guid`, durations, and dates of releases before 0.16);
 * - `number`: a `number`; `integer` is set for integer CLR types;
 * - `decimal`: a `string` in the `decimal` wire form (`"-1234.50"`), exact; its text uses the field's locale;
 * - `bigint`: a `bigint` (`long`, `ulong`, `BigInteger`);
 * - `date`: a `string` `YYYY-MM-DD` (`DateOnly`), the form of `<input type="date">`;
 * - `time`: a `string` `hh:mm:ss[.f]` (`TimeOnly`); its text drops zero seconds, as `<input type="time">` does;
 * - `datetime`: a `string` date and time (`DateTime`, and `DateTimeOffset` with `offset`); its text is the page's
 *   local date and time, the form of `<input type="datetime-local">`;
 * - `boolean`: a `boolean`, edited with a checkbox;
 * - `choice`: an enum name, one of `choices`;
 * - `value`: any other value (objects, lists, unions, custom codecs), which has no text form.
 *
 * Open: a later release may add kinds; treat an unknown kind like `value`.
 */
export type BridgeFieldKind = "text" | "number" | "decimal" | "bigint" | "boolean" | "date" | "time" | "datetime" | "choice" | "value";

/**
 * The C# validation attributes of a member (DataAnnotations), which a page can mirror on its input, such as
 * `maxlength` and `required`. They describe the .NET validation, which still runs; the field does not enforce them.
 */
export interface BridgeFieldConstraints {
  /** `[Required]`. */
  readonly required?: boolean;
  /** `[MinLength]`, or the minimum of `[StringLength]`. */
  readonly minLength?: number;
  /** `[MaxLength]` or `[StringLength]`. */
  readonly maxLength?: number;
  /** The minimum of `[Range]`: a number for numeric bounds, otherwise the bound's invariant text (`"0.01"`, a date). */
  readonly min?: number | string;
  /** The maximum of `[Range]`, like `min`. */
  readonly max?: number | string;
  /** `[Range(MinimumIsExclusive = true)]`. */
  readonly minExclusive?: boolean;
  /** `[Range(MaximumIsExclusive = true)]`. */
  readonly maxExclusive?: boolean;
  /**
   * `[RegularExpression]` when a browser's `pattern` attribute, which compiles it with the `v` flag, reads it as .NET
   * does; .NET matches it against the whole value, as the attribute does. A pattern with `\d`, `\w` or `\b`, inline
   * options, lookbehind or a character class the `v` flag rejects is left out.
   */
  readonly pattern?: string;
}

/**
 * A generated descriptor of one settable View member, such as `EditorFields.title`: how a field reads it from the
 * View's state, writes it, and finds its validation messages. The framework bindings' `useField` / `injectField`
 * and `createFieldController` take it.
 */
export interface BridgeField<TClient, TState, TValue> {
  /** The member's wire name. */
  readonly name: string;
  readonly kind: BridgeFieldKind;
  /** The member accepts `null`. */
  readonly nullable?: boolean;
  /** A `number` member of an integer CLR type. */
  readonly integer?: boolean;
  /** A `datetime` member whose values carry an offset (`DateTimeOffset`): a local text gets the page's offset. */
  readonly offset?: boolean;
  /** The member's DataAnnotations, when it has any the page can mirror. */
  readonly constraints?: BridgeFieldConstraints;
  /** A `choice` member's values, in declaration order. */
  readonly choices?: readonly TValue[];
  /**
   * The field has no setter, only its checked write: a field path below a member (`field-paths/1`), such as
   * `InvoiceFields.address.street`. A field controller then always writes it checked, as with `checked: true`.
   */
  readonly checkedOnly?: boolean;
  /** Reads the member from the View's state. */
  read(state: TState): TValue;
  /** Writes the member with its generated setter (an edit, last writer wins). */
  write(client: TClient, value: TValue): Promise<unknown>;
  /** Writes the member with its generated checked write, which reports a receipt. */
  checked?(client: TClient, value: TValue, options: FieldWriteOptions<TValue>): Promise<FieldWriteReceipt<TValue>>;
  /** The value and version of the checked field in the producer's state, which a checked write is based on. */
  baseline?(client: TClient): FieldBaseline<TValue>;
  /**
   * The member's validation messages in a state, present when the View has validation: its own messages, then
   * those of the values below it, such as the members of an object it holds.
   */
  errors?(state: TState): readonly string[];
  /**
   * Whether the member's validation is in progress in a state, present when the View has validation: an
   * asynchronous rule of the member, or of a value below it, is still checking (`validation-pending/1`).
   */
  validating?(state: TState): boolean;
}

const noMessages: readonly string[] = Object.freeze([]);
const cache = new WeakMap<object, Map<string, {
  readonly own: readonly string[] | undefined; readonly messages: readonly string[]; readonly translated: boolean; readonly version: number;
}>>();

/**
 * Whether the validation of the member `name`, or of a value below it, is in progress in `validation` (generated code
 * calls it for `BridgeField.validating`): a `validation.pending` path starts with `[name]`. `false` for a producer
 * before 0.16, which reports no pending validation.
 */
export function fieldValidating(validation: BridgeValidationState | undefined, name: string): boolean {
  const pending = validation?.pending;
  return pending !== undefined && pending.some(path => path.length > 0 && path[0] === name);
}

/**
 * The validation messages of the member `name` (generated code calls it for `BridgeField.errors`): the member's own
 * messages, `own` (its `{name}Errors`) when the View has them and none of the `validation` messages at `[name]` has
 * a text reference, and otherwise the `validation` messages at `[name]`,
 * followed by the `validation` messages whose path continues below `[name]`, in `validation` order. A message with a
 * text reference is shown in the window's language (`resolveText(error.text, error.message)`, `translation-text/1`).
 * Returns the same array for the same inputs and language, so a framework can compare it by identity.
 */
export function fieldErrors(validation: BridgeValidationState | undefined, name: string, own?: readonly string[]): readonly string[] {
  if (validation === undefined) return own ?? noMessages;
  let byName = cache.get(validation);
  const cached = byName?.get(name);
  const version = textVersion();
  if (cached !== undefined && cached.own === own && (!cached.translated || cached.version === version)) return cached.messages;
  // `{name}Errors` carries plain messages: the `validation` messages at `[name]` stand for them when one has a text.
  const ownText = own !== undefined && validation.errors.some(error =>
    error.text !== undefined && error.path.length === 1 && error.path[0] === name);
  const useOwn = own !== undefined && !ownText;
  let translated = false;
  const messages: string[] = useOwn ? [...own] : [];
  for (const error of validation.errors) {
    if (error.path.length === 0 || error.path[0] !== name) continue;
    if (error.path.length === 1 && useOwn) continue;
    if (error.text !== undefined) translated = true;
    messages.push(error.text === undefined ? error.message : resolveText(error.text, error.message));
  }
  const result = messages.length === 0 ? noMessages : useOwn && messages.length === own.length ? own : Object.freeze(messages);
  if (byName === undefined) cache.set(validation, byName = new Map());
  byName.set(name, { own, messages: result, translated, version });
  return result;
}
