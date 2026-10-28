// Validating decoders used by generated codecs, published as
// `@runic-artifex/views/generated/wire`. Each wire value type of
// specs/application/values.md has one, named after the value it carries; it accepts
// every valid form and returns the value in its normal form (or converts it, such as
// `int64` strings to bigint), and throws for anything else, so a malformed message
// never reaches application state.
//
// Each decoder is its own export. Generated code imports this module as a
// namespace (`import * as bridgeWire`), so a bundler keeps only the decoders a
// module calls.

// Longest text of an integer or decimal wire value (values.md, Scalars).
const maximumNumberTextLength = 4096;

function decodeBoolean(value: unknown): boolean {
  if (typeof value !== "boolean") throw new TypeError("Expected a boolean.");
  return value;
}

/** An integer wire type (`int8` … `uint32`): a number whose value is an integer within `minimum` … `maximum`. */
export function integer(value: unknown, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError("Expected a bounded integer.");
  return value === 0 ? 0 : value;
}

/** `float64`: any finite number; negative zero is returned as zero. */
export function finiteNumber(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new RangeError("Expected a finite number.");
  return value === 0 ? 0 : value;
}

/** `float32`: a number whose nearest single-precision value is finite; returns the shortest decimal that rounds to it. */
export function float32(value: unknown): number {
  const single = Math.fround(finiteNumber(value));
  if (!Number.isFinite(single)) throw new RangeError("Expected a single-precision number.");
  for (let precision = 1; precision < 9; precision++) {
    const shortest = Number(single.toPrecision(precision));
    if (Math.fround(shortest) === single) return shortest;
  }
  return Number(single.toPrecision(9));
}

function decodeString(value: unknown): string {
  if (typeof value !== "string") throw new TypeError("Expected a string.");
  return value;
}

function decodeBigInt(value: unknown, minimum?: string, maximum?: string): bigint {
  if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)$/.test(value)) throw new TypeError("Expected an integer string.");
  if (value.length > maximumNumberTextLength) throw new RangeError("Integer text is too long.");
  const result = BigInt(value);
  if (minimum !== undefined && result < BigInt(minimum)) throw new RangeError("Integer is below range.");
  if (maximum !== undefined && result > BigInt(maximum)) throw new RangeError("Integer is above range.");
  return result;
}

/** `int64`: an integer string within −2^63 … 2^63−1; returns the decimal digits, with `-0` as `0`. */
export function int64String(value: unknown): string {
  return decodeBigInt(value, "-9223372036854775808", "9223372036854775807").toString();
}

/** `decimal`: a decimal string with at most 28 fraction digits and 96 bits of digits; a negative zero loses its sign. */
export function decimal(value: unknown): string {
  if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(value)) throw new TypeError("Expected a decimal string.");
  if (value.length > maximumNumberTextLength) throw new RangeError("Decimal text is too long.");
  const [, fraction = ""] = value.split(".");
  if (fraction.length > 28) throw new RangeError("A decimal has more than 28 fraction digits.");
  const digits = BigInt(value.replace("-", "").replace(".", ""));
  if (digits >= 2n ** 96n) throw new RangeError("A decimal has more than 96 bits of digits.");
  return digits === 0n && value.startsWith("-") ? value.slice(1) : value;
}

/** `uuid`: 8-4-4-4-12 hexadecimal digits in either case; returns the lowercase normal form. */
export function uuid(value: unknown): string {
  const text = decodeString(value);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) throw new TypeError("Expected a UUID.");
  return text.toLowerCase();
}

const daysInMonths = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

/** Checks an RFC 3339 `full-date` from 0001-01-01 to 9999-12-31 and returns it. */
function fullDate(text: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (match === null) throw new TypeError("Expected a local date.");
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
  const length = month === 2 && leap ? 29 : daysInMonths[month - 1] ?? 0;
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > length) throw new RangeError("Expected a valid local date.");
  return text;
}

/** `local-date`: an RFC 3339 `full-date` from 0001-01-01 to 9999-12-31. */
export function localDate(value: unknown): string {
  return fullDate(decodeString(value));
}

/** Checks `hh:mm:ss` with an optional fraction and returns it without trailing zeros in the fraction. */
function partialTime(text: string): string {
  const match = /^(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(text);
  if (match === null) throw new TypeError("Expected a local time.");
  const [, hours = "", minutes = "", seconds = "", fraction = ""] = match;
  if (Number(hours) > 23 || Number(minutes) > 59 || Number(seconds) > 59) throw new RangeError("Expected a valid local time.");
  const digits = fraction.replace(/0+$/, "");
  return `${hours}:${minutes}:${seconds}${digits === "" ? "" : `.${digits}`}`;
}

/** `local-time`: `hh:mm:ss` with 0 to 7 fraction digits; returns the normal form without trailing zeros. */
export function localTime(value: unknown): string {
  return partialTime(decodeString(value));
}

/**
 * Reads a `date-time` into its normal date, time and offset. The offset is `""` for
 * a local date-time, otherwise `Z` or `±hh:mm` as read.
 */
function splitDateTime(value: unknown): { date: string; time: string; offset: string } {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)(Z|[+-]\d{2}:\d{2})?$/.exec(decodeString(value));
  if (match === null) throw new TypeError("Expected a date-time.");
  const [, date = "", time = "", offset = ""] = match;
  if (offset !== "" && offset !== "Z") {
    const [, hours = "", minutes = ""] = /^[+-](\d{2}):(\d{2})$/.exec(offset) ?? [];
    const total = Number(hours) * 60 + Number(minutes);
    if (Number(minutes) > 59 || total > 14 * 60) throw new RangeError("Expected an offset within ±14:00.");
  }
  return { date: fullDate(date), time: partialTime(time), offset };
}

/** `date-time`: a local date and time, followed by nothing, `Z` (UTC) or an offset within ±14:00, as read. */
export function dateTime(value: unknown): string {
  const { date, time, offset } = splitDateTime(value);
  return `${date}T${time}${offset}`;
}

/** `offset-date-time`: a date-time with a required offset; a zero offset becomes `Z`. */
export function offsetDateTime(value: unknown): string {
  const { date, time, offset } = splitDateTime(value);
  if (offset === "") throw new TypeError("Expected an offset date-time.");
  return `${date}T${time}${offset === "+00:00" || offset === "-00:00" ? "Z" : offset}`;
}

/**
 * `duration`: an ISO 8601 duration `[-]P[nD][T[nH][nM][n[.f]S]]` within ±2^63 100-ns units; returns the normal form,
 * such as `P1DT12H` for `PT36H`, which `Temporal.Duration.from` reads.
 */
export function duration(value: unknown): string {
  const match = /^(-?)P(?:(\d+)D)?(?:(T)(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)(?:\.(\d{1,7}))?S)?)?$/.exec(decodeString(value));
  if (match === null) throw new TypeError("Expected an ISO 8601 duration.");
  const [, sign = "", days, time, hours, minutes, seconds, fraction = ""] = match;
  if (days === undefined && time === undefined) throw new TypeError("Expected a duration component.");
  if (time !== undefined && hours === undefined && minutes === undefined && seconds === undefined) throw new TypeError("Expected a time component after T.");

  const ticks = BigInt(days ?? "0") * 864_000_000_000n + BigInt(hours ?? "0") * 36_000_000_000n
    + BigInt(minutes ?? "0") * 600_000_000n + BigInt(seconds ?? "0") * 10_000_000n + BigInt(fraction.padEnd(7, "0"));
  const negative = sign === "-";
  if (ticks > (negative ? 2n ** 63n : 2n ** 63n - 1n)) throw new RangeError("A duration is outside the 100-ns range.");

  let rest = ticks;
  const wholeDays = rest / 864_000_000_000n;
  rest %= 864_000_000_000n;
  const wholeHours = rest / 36_000_000_000n;
  rest %= 36_000_000_000n;
  const wholeMinutes = rest / 600_000_000n;
  rest %= 600_000_000n;
  const wholeSeconds = rest / 10_000_000n;
  const fractionDigits = (rest % 10_000_000n).toString().padStart(7, "0").replace(/0+$/, "");
  if (ticks === 0n) return "PT0S";

  const secondsText = wholeSeconds > 0n || fractionDigits !== ""
    ? `${wholeSeconds}${fractionDigits === "" ? "" : `.${fractionDigits}`}S`
    : "";
  const timeText = `${wholeHours > 0n ? `${wholeHours}H` : ""}${wholeMinutes > 0n ? `${wholeMinutes}M` : ""}${secondsText}`;
  return `${negative ? "-" : ""}P${wholeDays > 0n ? `${wholeDays}D` : ""}${timeText === "" ? "" : `T${timeText}`}`;
}

/** `enum`: one of the names the contract defines, compared exactly; the name is returned as it is. */
export function enumName<T extends string = string>(value: unknown, names?: readonly T[]): T {
  const text = decodeString(value);
  if (names !== undefined && !(names as readonly string[]).includes(text)) throw new RangeError("Unknown enum name.");
  return text as T;
}

/** An array of `T`: each element decoded with `decode`. */
export function array<T>(value: unknown, decode: (item: unknown) => T): readonly T[] {
  if (!Array.isArray(value)) throw new TypeError("Expected an array.");
  return value.map(decode);
}

function decodeObject<T>(value: unknown, decode: (item: Record<string, unknown>) => T): T {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Expected an object.");
  return decode(value as Record<string, unknown>);
}

/** A map of `T`: each member value decoded with `decode`, with any member name. */
export function stringRecord<T>(value: unknown, decode: (item: unknown) => T): Readonly<Record<string, T>> {
  const object = decodeObject(value, item => item);
  const result = Object.create(null) as Record<string, T>;
  for (const [key, item] of Object.entries(object)) result[key] = decode(item);
  return result;
}

/** A union: an object whose `$case` member names the case. */
export function union(value: unknown): any {
  const object = decodeObject(value, item => item);
  if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator.");
  return object;
}

/** A union written to the wire: an object whose `$case` member names the case. */
export function encodeUnion(value: unknown): Record<string, unknown> {
  const object = decodeObject(value, item => item);
  if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator.");
  return object;
}

// values.md: a value is nested at most 64 deep, counting containers only (a scalar has depth 0).
const maximumJsonDepth = 64;

// Lone surrogates are the only code points that the `u` flag matches here; a valid pair is one code point.
const loneSurrogate = /[\uD800-\uDFFF]/u;

/** Checks a JSON value at `level`, the number of containers that enclose it plus one. */
function checkJson(value: unknown, level: number): void {
  if (value === null || typeof value === "boolean") return;
  if (typeof value === "string") {
    if (loneSurrogate.test(value)) throw new TypeError("A JSON string cannot hold a lone surrogate.");
    return;
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new RangeError("A JSON number must be finite.");
    return;
  }
  if (level > maximumJsonDepth) throw new RangeError(`A JSON value is nested deeper than ${maximumJsonDepth} levels.`);
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      if (value[index] === undefined) throw new TypeError("A JSON array cannot hold undefined.");
      checkJson(value[index], level + 1);
    }
    return;
  }
  if (typeof value === "object") {
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) throw new TypeError("A JSON object must be a plain object.");
    // JSON.stringify leaves out members that are undefined, as for an optional member.
    for (const [name, item] of Object.entries(value)) {
      if (loneSurrogate.test(name)) throw new TypeError("A JSON member name cannot hold a lone surrogate.");
      if (item !== undefined) checkJson(item, level + 1);
    }
    return;
  }
  throw new TypeError(`A ${typeof value} is not a JSON value.`);
}

/**
 * Any JSON value, for a C# `JsonElement` or `JsonNode`: null, a boolean, a
 * finite number, a string, an array or a plain object of such values. The
 * value is returned as it is. With `nullable` false, as for a non-nullable
 * `JsonNode`, null is rejected.
 */
export function json(value: unknown, nullable?: true): unknown;
export function json(value: unknown, nullable: false): NonNullable<unknown>;
export function json(value: unknown, nullable = true): unknown {
  if (value === undefined) throw new TypeError("Expected a JSON value.");
  if (value === null && !nullable) throw new TypeError("Expected a JSON value other than null.");
  checkJson(value, 1);
  return value;
}

export { decodeBigInt as bigint, decodeBoolean as boolean, decodeObject as object, decodeString as string };
