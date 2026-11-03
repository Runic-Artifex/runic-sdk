// Text forms of field values (C1): how a field controller parses what the user typed and formats a value for a text
// input, by kind and, for numbers, by locale.
import type { BridgeFieldKind } from "./field.js";
import { FieldError } from "./field-error.js";

/** The traits of a field that select its text form. */
export interface FieldTextTraits<TValue> {
  readonly kind: BridgeFieldKind | undefined;
  readonly nullable: boolean;
  readonly integer: boolean;
  readonly choices: readonly TValue[] | undefined;
  /** A `datetime` member that requires an offset (`DateTimeOffset`). */
  readonly offset: boolean;
  /** The most digits a `number` or `decimal` text may have after the decimal separator. */
  readonly scale: number | undefined;
  /** The locale of `number`, `decimal` and `bigint` texts; `undefined` for the invariant form (`1.5`). */
  readonly locale: string | readonly string[] | undefined;
}

/** Parses a text for a field; `previous` is the value the field shows, which a `datetime` keeps the form of. */
export type FieldTextParser<TValue> = (text: string, previous: TValue) => TValue;

function invalid(message: string): FieldError { return new FieldError("invalid-text", message); }

interface NumberSymbols {
  readonly decimal: string;
  readonly group: string;
  readonly minus: string;
  /** The locale's digits 0–9, when they are not the ASCII digits. */
  readonly digits: string | undefined;
}

const invariantSymbols: NumberSymbols = Object.freeze({ decimal: ".", group: "", minus: "-", digits: undefined });
const symbolCache = new Map<string, NumberSymbols>();

/** The decimal separator, group separator, minus sign and digits a locale writes numbers with. */
function numberSymbols(locale: string | readonly string[] | undefined): NumberSymbols {
  if (locale === undefined) return invariantSymbols;
  const key = typeof locale === "string" ? locale : locale.join("\u0000");
  const cached = symbolCache.get(key);
  if (cached !== undefined) return cached;
  const format = new Intl.NumberFormat(locale as string | string[]);
  const parts = format.formatToParts(-12345.6);
  const part = (type: string, fallback: string) => parts.find(item => item.type === type)?.value ?? fallback;
  const digits = new Intl.NumberFormat(locale as string | string[], { useGrouping: false }).format(1234567890);
  const ordered = digits.length === 10 ? digits.slice(9) + digits.slice(0, 9) : "0123456789";
  const symbols = Object.freeze({
    decimal: part("decimal", "."),
    group: part("group", ""),
    minus: part("minusSign", "-"),
    digits: ordered === "0123456789" ? undefined : ordered,
  });
  symbolCache.set(key, symbols);
  return symbols;
}

const spaces = /[    ]/g;

/**
 * Reads a number typed in a locale into the invariant form (`-1234.5`): locale digits become ASCII digits, the
 * locale's minus sign `-`, its decimal separator `.`; group separators are dropped where they separate groups of
 * three digits of the integer part, and are an error elsewhere, so `1.5` is not read as 15 in de-DE.
 */
function delocalize(text: string, symbols: NumberSymbols): string {
  let result = text.trim();
  if (symbols === invariantSymbols) return result;
  if (symbols.digits !== undefined) {
    const digits = symbols.digits;
    result = [...result].map(character => {
      const index = digits.indexOf(character);
      return index < 0 ? character : String(index);
    }).join("");
  }
  result = result.replaceAll(symbols.minus, "-").replaceAll("−", "-");
  const groupIsSpace = symbols.group.length !== 0 && symbols.group.replace(spaces, "").length === 0;
  const group = groupIsSpace ? "\u0000" : symbols.group;
  if (groupIsSpace) result = result.replace(spaces, "\u0000");
  // The decimal separator: the locale's, and `.` where it is not the group separator (a `.` typed in fr-FR).
  const decimals = [symbols.decimal, ...symbols.decimal !== "." && group !== "." ? ["."] : []];
  let point = -1;
  for (const decimal of decimals) {
    const at = result.indexOf(decimal);
    if (at >= 0 && (point < 0 || at < point)) point = at;
  }
  let integer = point < 0 ? result : result.slice(0, point);
  const fraction = point < 0 ? "" : "." + result.slice(point + (decimals.find(decimal => result.startsWith(decimal, point))?.length ?? 1));
  if (group.length !== 0 && integer.includes(group)) {
    const sign = integer.startsWith("-") || integer.startsWith("+") ? integer[0]! : "";
    const groups = integer.slice(sign.length).split(group);
    if (!/^\d{1,3}$/.test(groups[0]!) || groups.slice(1).some(item => !/^\d{3}$/.test(item))) throw invalid(`${text.trim()} is not a number.`);
    integer = sign + groups.join("");
  }
  if (group.length !== 0 && fraction.slice(1).includes(group)) throw invalid(`${text.trim()} is not a number.`);
  return integer + fraction;
}

/** Writes an invariant number text (`-1234.5`) in a locale's digits, minus sign and decimal separator. */
function localize(text: string, symbols: NumberSymbols): string {
  if (symbols === invariantSymbols) return text;
  let result = "";
  for (const character of text) {
    if (character === "-") result += symbols.minus;
    else if (character === ".") result += symbols.decimal;
    else if (symbols.digits !== undefined && character >= "0" && character <= "9") result += symbols.digits[Number(character)];
    else result += character;
  }
  return result;
}

const twoPow96 = 2n ** 96n;

/** Reads an invariant decimal text into the `decimal` wire form; trailing zeros are kept, as they are on the wire. */
function decimalOf(text: string, shown: string, scale: number | undefined): string {
  const match = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(text);
  if (match === null || (match[2]!.length === 0 && (match[3] ?? "").length === 0)) throw invalid(`${shown} is not a number.`);
  const integer = match[2]!.replace(/^0+(?=\d)/, "") || "0";
  const fraction = match[3] ?? "";
  if (fraction.length > 28) throw invalid(`${shown} has more than 28 digits after the decimal separator.`);
  if (scale !== undefined && fraction.length > scale) throw invalid(`${shown} has more than ${scale} digits after the decimal separator.`);
  if (BigInt(integer + fraction) >= twoPow96) throw invalid(`${shown} is too large.`);
  const negative = match[1] === "-" && /[1-9]/.test(integer + fraction);
  return (negative ? "-" : "") + integer + (match[3] === undefined || fraction.length === 0 ? "" : "." + fraction);
}

const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/;
const timePattern = /^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/;
const dateTimePattern = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?)(Z|[+-]\d{2}:\d{2})?$/;

function checkDate(text: string, shown: string): { year: number; month: number; day: number } {
  const match = datePattern.exec(text);
  if (match === null) throw invalid(`${shown} is not a date (YYYY-MM-DD).`);
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const days = new Date(Date.UTC(2000, month, 0)).getUTCDate();
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > (month === 2 ? (leap ? 29 : 28) : days)) throw invalid(`${shown} is not a date.`);
  return { year, month, day };
}

/** The `local-time` normal form of `hh:mm[:ss[.f]]`. */
function normalTime(text: string, shown: string): { text: string; hours: number; minutes: number; seconds: number; fraction: string } {
  const match = timePattern.exec(text);
  if (match === null) throw invalid(`${shown} is not a time (hh:mm or hh:mm:ss).`);
  const [hours, minutes, seconds] = [Number(match[1]), Number(match[2]), Number(match[3] ?? "0")];
  if (hours > 23 || minutes > 59 || seconds > 59) throw invalid(`${shown} is not a time.`);
  const fraction = (match[4] ?? "").replace(/0+$/, "");
  return { text: `${match[1]}:${match[2]}:${match[3] ?? "00"}${fraction.length === 0 ? "" : "." + fraction}`, hours, minutes, seconds, fraction };
}

/** A time as a text input shows it: without seconds when they are zero, as `<input type="time">` does. */
function shortTime(time: string): string {
  return time.endsWith(":00") && time.length === 8 ? time.slice(0, 5) : time;
}

function pad(value: number, length = 2): string { return String(value).padStart(length, "0"); }

/** The page's offset from UTC at a local date and time, in minutes. */
function localOffset(year: number, month: number, day: number, hours: number, minutes: number, seconds: number): number {
  const date = new Date(0);
  date.setFullYear(year, month - 1, day);
  date.setHours(hours, minutes, seconds, 0);
  return -date.getTimezoneOffset();
}

function offsetText(minutes: number): string {
  if (minutes === 0) return "Z";
  const sign = minutes < 0 ? "-" : "+";
  const absolute = Math.abs(minutes);
  return `${sign}${pad(Math.floor(absolute / 60))}:${pad(absolute % 60)}`;
}

/** A `datetime` value with an offset, as the page's local date and time. */
function toLocal(value: string): string | undefined {
  const match = dateTimePattern.exec(value);
  if (match === null || match[3] === undefined) return undefined;
  const { year, month, day } = checkDate(match[1]!, value);
  const time = normalTime(match[2]!, value);
  const offset = match[3] === "Z" ? 0 : (match[3][0] === "-" ? -1 : 1) * (Number(match[3].slice(1, 3)) * 60 + Number(match[3].slice(4, 6)));
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(time.hours, time.minutes - offset, time.seconds, 0);
  const local = `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
    + `T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}${time.fraction.length === 0 ? "" : "." + time.fraction}`;
  return local;
}

function hasOffset(value: unknown): value is string {
  return typeof value === "string" && /(?:Z|[+-]\d{2}:\d{2})$/.test(value) && value.includes("T");
}

/**
 * The default parser of a field's kind. `number`, `decimal` and `bigint` read the locale's digits and separators;
 * `date`, `time` and `datetime` read the ISO forms that `<input type="date">`, `"time"` and `"datetime-local"` use.
 */
export function kindParser<TValue>(traits: FieldTextTraits<TValue>): FieldTextParser<TValue> {
  const { kind, nullable, integer, choices, scale } = traits;
  const empty = (text: string) => text.trim().length === 0;
  const required = (message: string): TValue => { if (nullable) return null as TValue; throw invalid(message); };
  switch (kind) {
    case "number": return text => {
      if (empty(text)) return required("Enter a number.");
      const shown = text.trim();
      const plain = delocalize(text, numberSymbols(traits.locale));
      // `Number` reads hexadecimal and binary forms and `Infinity`; a field reads decimal numbers only.
      if (!/^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(plain)) throw invalid(`${shown} is not a number.`);
      const value = Number(plain);
      if (!Number.isFinite(value)) throw invalid(`${shown} is not a number.`);
      if (integer && !Number.isSafeInteger(value)) throw invalid(`${shown} is not a whole number.`);
      if (scale !== undefined && /^[^e]*\.(\d+)/i.test(plain) && plain.replace(/e.*$/i, "").split(".")[1]!.length > scale)
        throw invalid(`${shown} has more than ${scale} digits after the decimal separator.`);
      return value as TValue;
    };
    case "decimal": return text => {
      if (empty(text)) return required("Enter a number.");
      const shown = text.trim();
      return decimalOf(delocalize(text, numberSymbols(traits.locale)), shown, scale) as TValue;
    };
    case "bigint": return text => {
      if (empty(text)) return required("Enter a whole number.");
      const shown = text.trim();
      const plain = delocalize(text, numberSymbols(traits.locale));
      if (!/^[+-]?\d+$/.test(plain)) throw invalid(`${shown} is not a whole number.`);
      return BigInt(plain) as TValue;
    };
    case "boolean": return text => {
      if (text === "true" || text === "false") return (text === "true") as TValue;
      if (nullable && empty(text)) return null as TValue;
      throw invalid(`${text} is not true or false.`);
    };
    case "choice": return text => {
      if (nullable && text.length === 0) return null as TValue;
      if (choices === undefined || choices.includes(text as TValue)) return text as TValue;
      throw invalid(`${text} is not one of ${choices.join(", ")}.`);
    };
    case "date": return text => {
      if (empty(text)) return required("Enter a date.");
      checkDate(text.trim(), text.trim());
      return text.trim() as TValue;
    };
    case "time": return text => {
      if (empty(text)) return required("Enter a time.");
      return normalTime(text.trim(), text.trim()).text as TValue;
    };
    case "datetime": return (text, previous) => {
      if (empty(text)) return required("Enter a date and time.");
      const shown = text.trim();
      const match = dateTimePattern.exec(shown);
      if (match === null) throw invalid(`${shown} is not a date and time (YYYY-MM-DDThh:mm).`);
      const { year, month, day } = checkDate(match[1]!, shown);
      const time = normalTime(match[2]!, shown);
      let zone = match[3];
      if (zone !== undefined) {
        const minutes = zone === "Z" ? 0 : Number(zone.slice(1, 3)) * 60 + Number(zone.slice(4, 6));
        if (minutes > 14 * 60 || Number(zone.slice(4, 6) || "0") > 59) throw invalid(`${shown} has an offset beyond ±14:00.`);
        // `offset-date-time` writes a zero offset as `Z`; `date-time` keeps the offset as read.
        if (traits.offset && minutes === 0) zone = "Z";
      } else if (traits.offset || hasOffset(previous)) {
        // A local date and time, as `<input type="datetime-local">` reports it: the page's offset at that time, or
        // UTC when the value it replaces is in UTC.
        const utc = typeof previous === "string" && previous.endsWith("Z") && !traits.offset;
        zone = utc ? "Z" : offsetText(localOffset(year, month, day, time.hours, time.minutes, time.seconds));
        if (utc) {
          const date = new Date(0);
          date.setFullYear(year, month - 1, day);
          date.setHours(time.hours, time.minutes, time.seconds, 0);
          return `${pad(date.getUTCFullYear(), 4)}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}${time.fraction.length === 0 ? "" : "." + time.fraction}Z` as TValue;
        }
      }
      return `${match[1]}T${time.text}${zone ?? ""}` as TValue;
    };
    case "value": return () => { throw invalid("This field has no text form."); };
    default: return text => text as TValue;
  }
}

/**
 * The default formatter of a field's kind: numbers in the locale's digits and decimal separator, without grouping;
 * times without zero seconds; date-times with an offset as the page's local date and time, which
 * `<input type="datetime-local">` shows.
 */
export function kindFormatter<TValue>(traits: FieldTextTraits<TValue>): (value: TValue) => string {
  const symbols = () => numberSymbols(traits.locale);
  return value => {
    switch (traits.kind) {
      case "number": case "bigint":
        return typeof value === "number" || typeof value === "bigint" ? localize(String(value), symbols()) : plain(value);
      case "decimal":
        return typeof value === "string" ? localize(value, symbols()) : plain(value);
      case "time":
        return typeof value === "string" ? shortTime(value) : plain(value);
      case "datetime": {
        if (typeof value !== "string") return plain(value);
        const local = toLocal(value) ?? value;
        const at = local.indexOf("T");
        return at < 0 ? local : local.slice(0, at + 1) + shortTime(local.slice(at + 1));
      }
      default: return plain(value);
    }
  };
}

/** The text of a value without a kind: strings as they are, numbers, bigints and booleans as `String` writes them. */
export function plain(value: unknown): string {
  switch (typeof value) {
    case "string": return value;
    case "number": case "bigint": case "boolean": return String(value);
    default: return "";
  }
}
