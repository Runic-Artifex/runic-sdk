// Text forms of field values (C1): how a field controller parses what the user typed and formats a value for a text
// input, by kind and, for numbers, dates and times, by locale.
import type { BridgeFieldKind } from "./field.js";
import { invalidText, type FieldErrorExpected } from "./field-error.js";

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
  /**
   * The locale of `number`, `decimal`, `bigint`, `date`, `time` and `datetime` texts; `undefined` for the invariant
   * form (`1.5`) and the ISO forms of the browser's date and time inputs.
   */
  readonly locale: string | readonly string[] | undefined;
}

/** Parses a text for a field; `previous` is the value the field shows, which a `datetime` keeps the form of. */
export type FieldTextParser<TValue> = (text: string, previous: TValue) => TValue;


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

/** Replaces a locale's digits with the ASCII digits. */
function asciiDigits(text: string, symbols: NumberSymbols): string {
  const digits = symbols.digits;
  if (digits === undefined) return text;
  return [...text].map(character => {
    const index = digits.indexOf(character);
    return index < 0 ? character : String(index);
  }).join("");
}

/**
 * Reads a number typed in a locale into the invariant form (`-1234.5`): locale digits become ASCII digits, the
 * locale's minus sign `-`, its decimal separator `.`; group separators are dropped where they separate groups of
 * three digits of the integer part, and are an error elsewhere, so `1.5` is not read as 15 in de-DE.
 */
function delocalize(text: string, symbols: NumberSymbols): string {
  if (symbols === invariantSymbols) return text.trim();
  let result = asciiDigits(text.trim(), symbols);
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
    if (!/^\d{1,3}$/.test(groups[0]!) || groups.slice(1).some(item => !/^\d{3}$/.test(item))) throw invalidText("not-a-number", { text: text.trim() });
    integer = sign + groups.join("");
  }
  if (group.length !== 0 && fraction.slice(1).includes(group)) throw invalidText("not-a-number", { text: text.trim() });
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

/**
 * The digits after the decimal point of an invariant number text once it is written without an exponent and without
 * trailing zeros: `1.50e2` (150) has none, `1e-7` has 7, `2.50` has 1.
 */
function fractionDigits(plain: string): number {
  const [mantissa = "", exponent = "0"] = plain.toLowerCase().split("e");
  const fraction = (mantissa.split(".")[1] ?? "").replace(/0+$/, "");
  return Math.max(0, fraction.length - Number(exponent));
}

const twoPow96 = 2n ** 96n;

/** Reads an invariant decimal text into the `decimal` wire form; trailing zeros are kept, as they are on the wire. */
function decimalOf(text: string, shown: string, scale: number | undefined): string {
  const match = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(text);
  if (match === null || (match[2]!.length === 0 && (match[3] ?? "").length === 0)) throw invalidText("not-a-number", { text: shown });
  const integer = match[2]!.replace(/^0+(?=\d)/, "") || "0";
  const fraction = match[3] ?? "";
  if (fraction.length > 28) throw invalidText("too-many-fraction-digits", { text: shown, scale: 28 });
  if (scale !== undefined && fraction.length > scale) throw invalidText("too-many-fraction-digits", { text: shown, scale });
  if (BigInt(integer + fraction) >= twoPow96) throw invalidText("out-of-range", { text: shown });
  const negative = match[1] === "-" && /[1-9]/.test(integer + fraction);
  return (negative ? "-" : "") + integer + (match[3] === undefined || fraction.length === 0 ? "" : "." + fraction);
}

const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/;
const timePattern = /^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/;
const dateTimePattern = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?)(Z|[+-]\d{2}:\d{2})?$/;

function checkDate(text: string, shown: string): { year: number; month: number; day: number } {
  const match = datePattern.exec(text);
  if (match === null) throw invalidText("not-a-date", { text: shown, patterns: ["YYYY-MM-DD"] });
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const days = new Date(Date.UTC(2000, month, 0)).getUTCDate();
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > (month === 2 ? (leap ? 29 : 28) : days)) throw invalidText("not-a-date", { text: shown });
  return { year, month, day };
}

/** The `local-time` normal form of `hh:mm[:ss[.f]]`. */
function normalTime(text: string, shown: string): { text: string; hours: number; minutes: number; seconds: number; fraction: string } {
  const match = timePattern.exec(text);
  if (match === null) throw invalidText("not-a-time", { text: shown, patterns: ["hh:mm", "hh:mm:ss"] });
  const [hours, minutes, seconds] = [Number(match[1]), Number(match[2]), Number(match[3] ?? "0")];
  if (hours > 23 || minutes > 59 || seconds > 59) throw invalidText("not-a-time", { text: shown });
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

/** A local date and time read as if it were UTC, in milliseconds since the epoch. */
function utcMilliseconds(year: number, month: number, day: number, hours: number, minutes: number, seconds: number): number {
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hours, minutes, seconds, 0);
  return date.getTime();
}

/** The page's offsets, in minutes, at which a local date and time (read as UTC, `asUtc`) exists: two in a DST overlap. */
function localOffsets(asUtc: number): number[] {
  const probed = new Set<number>();
  for (let hours = -15; hours <= 15; hours += 3) probed.add(-new Date(asUtc + hours * 3_600_000).getTimezoneOffset());
  return [...probed].filter(offset => -new Date(asUtc - offset * 60_000).getTimezoneOffset() === offset);
}

/** The offset of a date-time value in minutes, or `undefined` without one. */
function offsetOf(value: unknown): number | undefined {
  if (typeof value !== "string") return undefined;
  const zone = /(Z|[+-]\d{2}:\d{2})$/.exec(value)?.[1];
  if (zone === undefined || !value.includes("T")) return undefined;
  return zone === "Z" ? 0 : (zone[0] === "-" ? -1 : 1) * (Number(zone.slice(1, 3)) * 60 + Number(zone.slice(4, 6)));
}

/** The instant of a date-time value with an offset, in milliseconds since the epoch. */
function instantOf(value: string): number | undefined {
  const match = dateTimePattern.exec(value);
  const offset = offsetOf(value);
  if (match === null || offset === undefined) return undefined;
  const [year, month, day] = match[1]!.split("-").map(Number) as [number, number, number];
  const [hours, minutes, seconds] = match[2]!.split(":").map(part => Math.trunc(Number(part))) as [number, number, number?];
  return utcMilliseconds(year, month, day, hours, minutes, seconds ?? 0) - offset * 60_000;
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

/** A part of a locale's date or time text: a unit, or the literal text between units. */
type DatePiece = { readonly unit: "year" | "month" | "day" | "hour" | "minute" | "second" | "dayPeriod" } | { readonly literal: string };

/**
 * How a locale writes dates and times, from `Intl.DateTimeFormat#formatToParts` in the Gregorian calendar: the order
 * of the units and the literals between them, with and without seconds, its hour cycle and day periods.
 */
interface DateTextForm {
  readonly date: readonly DatePiece[];
  readonly time: readonly DatePiece[];
  readonly timeSeconds: readonly DatePiece[];
  readonly dateTime: readonly DatePiece[];
  readonly dateTimeSeconds: readonly DatePiece[];
  /** `h11` and `h12` write hours with a day period; `h23` and `h24` without, as 0–23. */
  readonly twelveHours: boolean;
  /** `h11` writes midnight and noon as 0, `h12` as 12. */
  readonly zeroHour: boolean;
  readonly padHour: boolean;
  readonly am: string;
  readonly pm: string;
  readonly symbols: NumberSymbols;
  /** Every character of the locale's literals, which a typed text may use between its numbers. */
  readonly literals: ReadonlySet<string>;
}

const formCache = new Map<string, DateTextForm>();
const bidiMarks = /[‎‏؜‪-‮⁦-⁩]/g;
const anySpace = /[\s   ]+/g;

function cleanText(text: string): string { return text.replace(bidiMarks, "").replace(anySpace, " "); }

/** The date and time form of a locale, or `undefined` for the ISO forms of the browser's inputs. */
function dateTextForm(locale: string | readonly string[] | undefined): DateTextForm | undefined {
  if (locale === undefined) return undefined;
  const key = typeof locale === "string" ? locale : locale.join("\u0000");
  const cached = formCache.get(key);
  if (cached !== undefined) return cached;
  const locales = locale as string | string[];
  // A day after the 12th and an evening hour, so that no two units have the same text.
  const probe = new Date(Date.UTC(2026, 10, 23, 21, 45, 56));
  const base = { timeZone: "UTC", calendar: "gregory" } as const;
  const dateUnits = { year: "numeric", month: "2-digit", day: "2-digit" } as const;
  const timeUnits = { hour: "numeric", minute: "2-digit" } as const;
  const literals = new Set<string>();
  const pieces = (options: Intl.DateTimeFormatOptions, required: readonly string[]): DatePiece[] => {
    const result: DatePiece[] = [];
    for (const part of new Intl.DateTimeFormat(locales, { ...base, ...options }).formatToParts(probe)) {
      if (part.type === "literal") {
        const literal = cleanText(part.value);
        if (literal.length === 0) continue;
        for (const character of literal) literals.add(character);
        const last = result.at(-1);
        if (last !== undefined && "literal" in last) result[result.length - 1] = { literal: last.literal + literal };
        else result.push({ literal });
      } else if (["year", "month", "day", "hour", "minute", "second", "dayPeriod"].includes(part.type)) {
        result.push({ unit: part.type as "year" });
      }
    }
    const units = result.flatMap(piece => "unit" in piece ? [piece.unit] : []);
    // A locale whose parts lack a unit or repeat one is written in the ISO order.
    if (required.some(unit => units.filter(item => item === unit).length !== 1)) {
      const iso: DatePiece[] = [];
      for (const unit of required) {
        if (iso.length !== 0) iso.push({ literal: unit === "hour" ? " " : unit === "minute" || unit === "second" ? ":" : "-" });
        iso.push({ unit: unit as "year" });
      }
      return iso;
    }
    return result;
  };
  // The locale's short time style: its hour cycle, and whether it pads hours (`09:05` in `de`, `9:05` in `ja`).
  const time = new Intl.DateTimeFormat(locales, { ...base, timeStyle: "short" });
  const cycle = time.resolvedOptions().hourCycle ?? "h23";
  const twelveHours = cycle === "h11" || cycle === "h12";
  const hourAt = (hours: number) => time.formatToParts(new Date(Date.UTC(2026, 10, 23, hours, 5))).find(part => part.type === "hour")?.value ?? "";
  const period = (hours: number, fallback: string) => cleanText(new Intl.DateTimeFormat(locales, { ...base, hour: "numeric", hour12: true })
    .formatToParts(new Date(Date.UTC(2026, 10, 23, hours))).find(part => part.type === "dayPeriod")?.value ?? fallback).trim() || fallback;
  const symbols = numberSymbols(locale);
  const dateRequired = ["year", "month", "day"];
  const timeRequired = twelveHours ? ["hour", "minute", "dayPeriod"] : ["hour", "minute"];
  const timeSecondsRequired = [...timeRequired.slice(0, 2), "second", ...timeRequired.slice(2)];
  const form: DateTextForm = Object.freeze({
    date: pieces(dateUnits, dateRequired),
    time: pieces(timeUnits, timeRequired),
    timeSeconds: pieces({ ...timeUnits, second: "2-digit" }, timeSecondsRequired),
    dateTime: pieces({ ...dateUnits, ...timeUnits }, [...dateRequired, ...timeRequired]),
    dateTimeSeconds: pieces({ ...dateUnits, ...timeUnits, second: "2-digit" }, [...dateRequired, ...timeSecondsRequired]),
    twelveHours,
    zeroHour: cycle === "h11",
    padHour: asciiDigits(hourAt(9), symbols).length === 2,
    am: period(9, "AM"),
    pm: period(21, "PM"),
    symbols,
    literals,
  });
  formCache.set(key, form);
  return form;
}

interface LocalParts {
  readonly year?: number | undefined; readonly month?: number | undefined; readonly day?: number | undefined;
  readonly hours?: number | undefined; readonly minutes?: number | undefined; readonly seconds?: number | undefined;
  readonly fraction?: string | undefined;
}

/** Writes a date or time in a locale's form, with the locale's digits. */
function writeLocal(form: DateTextForm, pieces: readonly DatePiece[], parts: LocalParts): string {
  const digits = (text: string) => localize(text, form.symbols);
  let result = "";
  for (const piece of pieces) {
    if ("literal" in piece) { result += piece.literal; continue; }
    const hours = parts.hours ?? 0;
    switch (piece.unit) {
      case "year": result += digits(pad(parts.year ?? 0, 4)); break;
      case "month": result += digits(pad(parts.month ?? 0)); break;
      case "day": result += digits(pad(parts.day ?? 0)); break;
      case "hour": {
        const shown = form.twelveHours ? (hours % 12 === 0 && !form.zeroHour ? 12 : hours % 12) : hours;
        result += digits(form.padHour ? pad(shown) : String(shown));
        break;
      }
      case "minute": result += digits(pad(parts.minutes ?? 0)); break;
      case "second":
        result += digits(pad(parts.seconds ?? 0));
        if (parts.fraction !== undefined && parts.fraction.length !== 0) result += form.symbols.decimal + digits(parts.fraction);
        break;
      case "dayPeriod": result += hours < 12 ? form.am : form.pm; break;
    }
  }
  return result;
}

/** The pattern of a locale's form for messages, such as `DD.MM.YYYY` or `MM/DD/YYYY, h:mm AM`. */
function patternOf(form: DateTextForm, pieces: readonly DatePiece[]): string {
  const names = { year: "YYYY", month: "MM", day: "DD", hour: form.padHour ? "hh" : "h", minute: "mm", second: "ss", dayPeriod: form.am };
  return pieces.map(piece => "literal" in piece ? piece.literal : names[piece.unit]).join("").trim();
}

/**
 * Reads a date or time typed in a locale's form: its numbers in the order of `candidates` (the form with or without
 * seconds), between any of the locale's literals or `. , / - :` and spaces, a fraction of a second after a decimal
 * separator, with the locale's day period or `am`/`pm`.
 * A year has four digits, minutes and seconds two; without a day period the hour is read as 0–23, as in the ISO form.
 * Returns `undefined` for a text that is not in the form; the caller checks the date's range.
 */
function readLocal(form: DateTextForm, text: string, candidates: readonly (readonly DatePiece[])[]): LocalParts | undefined {
  let rest = asciiDigits(cleanText(text), form.symbols).trim();
  let afternoon: boolean | undefined;
  if (candidates.some(pieces => pieces.some(piece => "unit" in piece && piece.unit === "hour"))) {
    const periods: [string, boolean][] = [[form.am, false], [form.pm, true], ["a.m.", false], ["p.m.", true], ["am", false], ["pm", true]];
    periods.sort((left, right) => right[0].length - left[0].length);
    const lower = rest.toLocaleLowerCase();
    for (const [marker, pm] of periods) {
      const at = lower.indexOf(marker.toLocaleLowerCase());
      if (marker.length === 0 || at < 0) continue;
      if (lower.indexOf(marker.toLocaleLowerCase(), at + 1) >= 0) return undefined;
      afternoon = pm;
      rest = `${rest.slice(0, at)} ${rest.slice(at + marker.length)}`;
      break;
    }
  }
  const numbers: { text: string; at: number }[] = [];
  let residue = "";
  let last = 0;
  for (const match of rest.matchAll(/\d+/g)) {
    residue += rest.slice(last, match.index);
    numbers.push({ text: match[0], at: match.index });
    last = match.index + match[0].length;
  }
  residue += rest.slice(last);
  if ([...residue].some(character => !" .,/-:".includes(character) && character !== form.symbols.decimal && !form.literals.has(character))) return undefined;
  for (const pieces of candidates) {
    const units = pieces.flatMap(piece => "unit" in piece && piece.unit !== "dayPeriod" ? [piece.unit] : []);
    let fraction: string | undefined;
    let values = numbers;
    if (units.at(-1) === "second" && numbers.length === units.length + 1) {
      // A fraction of a second: digits right after the seconds and a `.`, `,` or the locale's decimal separator.
      const seconds = numbers[units.length - 1]!;
      const separator = rest.slice(seconds.at + seconds.text.length, numbers[units.length]!.at);
      if (separator !== "." && separator !== "," && separator !== form.symbols.decimal) continue;
      fraction = numbers[units.length]!.text;
      if (fraction.length > 7) return undefined;
      values = numbers.slice(0, units.length);
    }
    if (values.length !== units.length) continue;
    const parts: Record<string, number> = {};
    let fits = true;
    units.forEach((unit, index) => {
      const value = values[index]!.text;
      const length = unit === "year" ? [4, 4] : unit === "minute" || unit === "second" ? [2, 2] : [1, 2];
      if (value.length < length[0]! || value.length > length[1]!) fits = false;
      parts[unit] = Number(value);
    });
    if (!fits) return undefined;
    let hours = parts["hour"];
    if (hours !== undefined && afternoon !== undefined) {
      if (hours > 12) return undefined;
      hours = hours % 12 + (afternoon ? 12 : 0);
    }
    return { year: parts["year"], month: parts["month"], day: parts["day"], hours, minutes: parts["minute"], seconds: parts["second"], fraction };
  }
  return undefined;
}

function isoDateOf(parts: LocalParts): string { return `${pad(parts.year!, 4)}-${pad(parts.month!)}-${pad(parts.day!)}`; }

function isoTimeOf(parts: LocalParts): string {
  return `${pad(parts.hours!)}:${pad(parts.minutes!)}${parts.seconds === undefined ? "" : ":" + pad(parts.seconds)}`
    + (parts.fraction === undefined ? "" : "." + parts.fraction);
}

/** The ISO text of a date or time typed in a locale's form or the ISO form, or `undefined` for neither. */
function isoText(traits: FieldTextTraits<unknown>, shown: string, kind: "date" | "time" | "datetime"): string | undefined {
  const form = dateTextForm(traits.locale);
  if (form === undefined) return shown;
  const ascii = asciiDigits(cleanText(shown), form.symbols).trim();
  const iso = kind === "date" ? datePattern : kind === "time" ? timePattern : dateTimePattern;
  if (iso.test(ascii)) return ascii;
  const parts = readLocal(form, shown, kind === "date" ? [form.date] : kind === "time" ? [form.time, form.timeSeconds] : [form.dateTime, form.dateTimeSeconds]);
  if (parts === undefined) return undefined;
  return kind === "date" ? isoDateOf(parts) : kind === "time" ? isoTimeOf(parts) : `${isoDateOf(parts)}T${isoTimeOf(parts)}`;
}

/** The pattern of a kind's text for messages: the locale's form and the ISO form, or the ISO form alone. */
function patternHint(traits: FieldTextTraits<unknown>, kind: "date" | "time" | "datetime"): readonly string[] {
  const iso = kind === "date" ? ["YYYY-MM-DD"] : kind === "time" ? ["hh:mm", "hh:mm:ss"] : ["YYYY-MM-DDThh:mm"];
  const form = dateTextForm(traits.locale);
  if (form === undefined) return iso;
  const pattern = patternOf(form, kind === "date" ? form.date : kind === "time" ? form.time : form.dateTime);
  return pattern === iso[0] && iso.length === 1 ? iso : [pattern, ...iso];
}

/** Writes an ISO date, time or local date and time in a locale's form, or returns it when it is not one. */
function localText(form: DateTextForm, iso: string, kind: "date" | "time" | "datetime"): string {
  const dateText = kind === "time" ? undefined : kind === "date" ? iso : iso.slice(0, iso.indexOf("T"));
  const timeText = kind === "date" ? undefined : kind === "time" ? iso : iso.slice(iso.indexOf("T") + 1);
  const date = dateText === undefined ? undefined : datePattern.exec(dateText);
  const time = timeText === undefined ? undefined : timePattern.exec(timeText);
  if (date === null || time === null || (kind === "datetime" && !iso.includes("T"))) return iso;
  const fraction = (time?.[4] ?? "").replace(/0+$/, "");
  const seconds = time === undefined ? 0 : Number(time[3] ?? "0");
  const parts: LocalParts = {
    year: date === undefined ? undefined : Number(date[1]), month: date === undefined ? undefined : Number(date[2]),
    day: date === undefined ? undefined : Number(date[3]),
    hours: time === undefined ? undefined : Number(time[1]), minutes: time === undefined ? undefined : Number(time[2]),
    seconds, fraction,
  };
  const long = seconds !== 0 || fraction.length !== 0;
  const pieces = kind === "date" ? form.date : kind === "time" ? (long ? form.timeSeconds : form.time) : (long ? form.dateTimeSeconds : form.dateTime);
  return writeLocal(form, pieces, parts);
}

function hasOffset(value: unknown): value is string {
  return typeof value === "string" && /(?:Z|[+-]\d{2}:\d{2})$/.test(value) && value.includes("T");
}

/**
 * The default parser of a field's kind. `number`, `decimal` and `bigint` read the locale's digits and separators;
 * `date`, `time` and `datetime` read the ISO forms that `<input type="date">`, `"time"` and `"datetime-local"` use,
 * and with a locale also the locale's form (`23.11.2026`, `9:45 PM`).
 */
export function kindParser<TValue>(traits: FieldTextTraits<TValue>): FieldTextParser<TValue> {
  const { kind, nullable, integer, choices, scale } = traits;
  const empty = (text: string) => text.trim().length === 0;
  const required = (expected: FieldErrorExpected): TValue => { if (nullable) return null as TValue; throw invalidText("required", { expected }); };
  switch (kind) {
    case "number": return text => {
      if (empty(text)) return required("number");
      const shown = text.trim();
      const plain = delocalize(text, numberSymbols(traits.locale));
      // `Number` reads hexadecimal and binary forms and `Infinity`; a field reads decimal numbers only.
      if (!/^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(plain)) throw invalidText("not-a-number", { text: shown });
      const value = Number(plain);
      if (!Number.isFinite(value)) throw invalidText("not-a-number", { text: shown });
      if (integer && !Number.isSafeInteger(value)) throw invalidText("not-a-whole-number", { text: shown });
      if (scale !== undefined && fractionDigits(plain) > scale)
        throw invalidText("too-many-fraction-digits", { text: shown, scale });
      return value as TValue;
    };
    case "decimal": return text => {
      if (empty(text)) return required("number");
      const shown = text.trim();
      return decimalOf(delocalize(text, numberSymbols(traits.locale)), shown, scale) as TValue;
    };
    case "bigint": return text => {
      if (empty(text)) return required("whole-number");
      const shown = text.trim();
      const plain = delocalize(text, numberSymbols(traits.locale));
      if (!/^[+-]?\d+$/.test(plain)) throw invalidText("not-a-whole-number", { text: shown });
      return BigInt(plain) as TValue;
    };
    case "boolean": return text => {
      if (text === "true" || text === "false") return (text === "true") as TValue;
      if (nullable && empty(text)) return null as TValue;
      throw invalidText("not-a-boolean", { text });
    };
    case "choice": return text => {
      if (nullable && text.length === 0) return null as TValue;
      if (choices === undefined || choices.includes(text as TValue)) return text as TValue;
      throw invalidText("not-a-choice", { text, choices: choices.map(String) });
    };
    case "date": return text => {
      if (empty(text)) return required("date");
      const shown = text.trim();
      const iso = isoText(traits, shown, "date");
      if (iso === undefined || !datePattern.test(iso)) throw invalidText("not-a-date", { text: shown, patterns: patternHint(traits, "date") });
      checkDate(iso, shown);
      return iso as TValue;
    };
    case "time": return text => {
      if (empty(text)) return required("time");
      const shown = text.trim();
      const iso = isoText(traits, shown, "time");
      if (iso === undefined || !timePattern.test(iso)) throw invalidText("not-a-time", { text: shown, patterns: patternHint(traits, "time") });
      return normalTime(iso, shown).text as TValue;
    };
    case "datetime": {
      const shownText = kindFormatter(traits);
      return (text, previous) => {
      if (empty(text)) return required("date-time");
      const shown = text.trim();
      // The text the field shows for its value means that value, with its offset and fraction.
      if (typeof previous === "string" && previous.length !== 0 && shownText(previous) === shown) return previous;
      const match = dateTimePattern.exec(isoText(traits, shown, "datetime") ?? "");
      if (match === null) throw invalidText("not-a-date-time", { text: shown, patterns: patternHint(traits, "datetime") });
      const { year, month, day } = checkDate(match[1]!, shown);
      const time = normalTime(match[2]!, shown);
      let zone = match[3];
      if (zone !== undefined) {
        const minutes = zone === "Z" ? 0 : Number(zone.slice(1, 3)) * 60 + Number(zone.slice(4, 6));
        if (minutes > 14 * 60 || Number(zone.slice(4, 6) || "0") > 59) throw invalidText("offset-out-of-range", { text: shown });
        // `offset-date-time` writes a zero offset as `Z`; `date-time` keeps the offset as read.
        if (traits.offset && minutes === 0) zone = "Z";
      } else if (traits.offset || hasOffset(previous)) {
        // A local date and time, as `<input type="datetime-local">` reports it: the page's offset at that time, or
        // UTC when the value it replaces is in UTC.
        const utc = typeof previous === "string" && previous.endsWith("Z") && !traits.offset;
        const asUtc = utcMilliseconds(year, month, day, time.hours, time.minutes, time.seconds);
        const candidates = localOffsets(asUtc);
        const previousOffset = offsetOf(previous);
        let chosen = localOffset(year, month, day, time.hours, time.minutes, time.seconds);
        if (candidates.length > 1 && previousOffset !== undefined) {
          // A local time that occurs twice (a DST overlap): the offset of the value it replaces when that is one of
          // them, and for a UTC value the instant nearest it, so an edit does not move the value by an hour.
          if (!utc && candidates.includes(previousOffset)) chosen = previousOffset;
          else if (utc) {
            const before = instantOf(previous as string);
            if (before !== undefined)
              chosen = candidates.reduce((best, offset) => Math.abs(asUtc - offset * 60_000 - before) < Math.abs(asUtc - best * 60_000 - before) ? offset : best);
          }
        }
        if (utc) {
          const date = new Date(asUtc - chosen * 60_000);
          return `${pad(date.getUTCFullYear(), 4)}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}${time.fraction.length === 0 ? "" : "." + time.fraction}Z` as TValue;
        }
        zone = offsetText(chosen);
      }
      return `${match[1]}T${time.text}${zone ?? ""}` as TValue;
      };
    }
    case "value": return () => { throw invalidText("no-text-form"); };
    default: return text => text as TValue;
  }
}

/**
 * The default formatter of a field's kind: numbers in the locale's digits and decimal separator, without grouping;
 * times without zero seconds; date-times with an offset as the page's local date and time, which
 * `<input type="datetime-local">` shows. With a locale, dates and times are written in its numeric form, in the
 * Gregorian calendar with a four-digit year (`23.11.2026, 21:45` in `de`, `11/23/2026, 9:45 PM` in `en-US`).
 */
export function kindFormatter<TValue>(traits: FieldTextTraits<TValue>): (value: TValue) => string {
  const symbols = () => numberSymbols(traits.locale);
  return value => {
    switch (traits.kind) {
      case "number": case "bigint":
        return typeof value === "number" || typeof value === "bigint" ? localize(String(value), symbols()) : plain(value);
      case "decimal":
        return typeof value === "string" ? localize(value, symbols()) : plain(value);
      case "date": {
        const form = dateTextForm(traits.locale);
        return typeof value !== "string" ? plain(value) : form === undefined ? value : localText(form, value, "date");
      }
      case "time": {
        if (typeof value !== "string") return plain(value);
        const form = dateTextForm(traits.locale);
        return form === undefined ? shortTime(value) : localText(form, value, "time");
      }
      case "datetime": {
        if (typeof value !== "string") return plain(value);
        const local = toLocal(value) ?? value;
        const form = dateTextForm(traits.locale);
        const match = form === undefined ? null : dateTimePattern.exec(local);
        // A value whose offset the page cannot show as a local time keeps its ISO form.
        if (form !== undefined && match !== null && match[3] === undefined) return localText(form, `${match[1]}T${match[2]}`, "datetime");
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
