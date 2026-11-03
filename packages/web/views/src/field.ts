import type { FieldBaseline, FieldWriteOptions, FieldWriteReceipt } from "./connection.js";
import type { BridgeValidationState } from "./validation.js";

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
   * `[RegularExpression]` when its pattern means the same in JavaScript, which .NET matches against the whole value,
   * as the HTML `pattern` attribute does.
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
}

const noMessages: readonly string[] = Object.freeze([]);
const cache = new WeakMap<object, Map<string, { readonly own: readonly string[] | undefined; readonly messages: readonly string[] }>>();

/**
 * The validation messages of the member `name` (generated code calls it for `BridgeField.errors`): the member's own
 * messages, `own` (its `{name}Errors`) when the View has them and otherwise the `validation` messages at `[name]`,
 * followed by the `validation` messages whose path continues below `[name]`, in `validation` order. Returns the same
 * array for the same inputs, so a framework can compare it by identity.
 */
export function fieldErrors(validation: BridgeValidationState | undefined, name: string, own?: readonly string[]): readonly string[] {
  if (validation === undefined) return own ?? noMessages;
  let byName = cache.get(validation);
  const cached = byName?.get(name);
  if (cached !== undefined && cached.own === own) return cached.messages;
  const messages: string[] = own === undefined ? [] : [...own];
  for (const error of validation.errors) {
    if (error.path.length === 0 || error.path[0] !== name) continue;
    if (error.path.length === 1 && own !== undefined) continue;
    messages.push(error.message);
  }
  const result = messages.length === 0 ? noMessages : own !== undefined && messages.length === own.length ? own : Object.freeze(messages);
  if (byName === undefined) cache.set(validation, byName = new Map());
  byName.set(name, { own, messages: result });
  return result;
}
