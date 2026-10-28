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

function decodeBoolean(value: unknown): boolean {
  if (typeof value !== "boolean") throw new TypeError("Expected a boolean.");
  return value;
}

/** An integer wire type (`int8` … `uint32`): a number whose value is an integer within `minimum` … `maximum`. */
export function integer(value: unknown, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError("Expected a bounded integer.");
  return value;
}

/** `float64`: any finite number. */
export function finiteNumber(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new RangeError("Expected a finite number.");
  return value;
}

/** `float32`: a number whose nearest single-precision value is finite; returns the shortest number that rounds to it. */
export function float32(value: unknown): number {
  const number = finiteNumber(value);
  const single = Math.fround(number);
  if (!Number.isFinite(single)) throw new RangeError("Expected a single-precision number.");
  return single;
}

function decodeString(value: unknown): string {
  if (typeof value !== "string") throw new TypeError("Expected a string.");
  return value;
}

function decodeBigInt(value: unknown, minimum?: string, maximum?: string): bigint {
  if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)$/.test(value)) throw new TypeError("Expected an integer string.");
  const result = BigInt(value);
  if (minimum !== undefined && result < BigInt(minimum)) throw new RangeError("Integer is below range.");
  if (maximum !== undefined && result > BigInt(maximum)) throw new RangeError("Integer is above range.");
  return result;
}

export function int64String(value: unknown): string {
  decodeBigInt(value, "-9223372036854775808", "9223372036854775807");
  return value as string;
}

export function decimal(value: unknown): string {
  if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(value)) throw new TypeError("Expected a decimal string.");
  return value;
}

/** `uuid`: 8-4-4-4-12 hexadecimal digits in either case; returns the lowercase normal form. */
export function uuid(value: unknown): string {
  const text = decodeString(value);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) throw new TypeError("Expected a UUID.");
  return text.toLowerCase();
}

/** `local-date`: an RFC 3339 `full-date` from 0001-01-01 to 9999-12-31. */
export function localDate(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) throw new TypeError("Expected a local date.");
  return text;
}

/** `local-time`: `hh:mm:ss` with 0 to 7 fraction digits; returns the normal form without trailing zeros. */
export function localTime(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?$/.test(text)) throw new TypeError("Expected a local time.");
  return text;
}

/** `date-time`: a local date and time, followed by nothing, `Z` or an offset. */
export function dateTime(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{4}-\d{2}-\d{2}T/.test(text)) throw new TypeError("Expected a date-time.");
  return text;
}

/** `offset-date-time`: a date-time with a required offset; a zero offset becomes `Z`. */
export function offsetDateTime(value: unknown): string {
  const text = dateTime(value);
  if (!/(Z|[+-]\d{2}:\d{2})$/.test(text)) throw new TypeError("Expected an offset date-time.");
  return text;
}

/**
 * `duration`: an ISO 8601 duration `[-]P[nD][T[nH][nM][n[.f]S]]` within ±2^63 100-ns units; returns the normal form,
 * such as `P1DT12H` for `PT36H`, which `Temporal.Duration.from` reads.
 */
export function duration(value: unknown): string {
  const text = decodeString(value);
  if (!/^-?P/.test(text)) throw new TypeError("Expected an ISO 8601 duration.");
  return text;
}

export function enumName<T extends string = string>(value: unknown, names?: readonly T[]): T {
  const text = decodeString(value);
  if (names !== undefined && !(names as readonly string[]).includes(text)) throw new RangeError("Unknown enum name.");
  return text as T;
}

export function array<T>(value: unknown, decode: (item: unknown) => T): readonly T[] {
  if (!Array.isArray(value)) throw new TypeError("Expected an array.");
  return value.map(decode);
}

function decodeObject<T>(value: unknown, decode: (item: Record<string, unknown>) => T): T {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Expected an object.");
  return decode(value as Record<string, unknown>);
}

export function stringRecord<T>(value: unknown, decode: (item: unknown) => T): Readonly<Record<string, T>> {
  const object = decodeObject(value, item => item);
  const result = Object.create(null) as Record<string, T>;
  for (const [key, item] of Object.entries(object)) result[key] = decode(item);
  return result;
}

export function union(value: unknown): any {
  const object = decodeObject(value, item => item);
  if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator.");
  return object;
}

export function encodeUnion(value: unknown): Record<string, unknown> {
  const object = decodeObject(value, item => item);
  if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator.");
  return object;
}

// .NET reads JSON up to this depth (JsonDocumentOptions.MaxDepth).
const maximumJsonDepth = 64;

function checkJson(value: unknown, depth: number): void {
  if (depth > maximumJsonDepth) throw new RangeError(`A JSON value is nested deeper than ${maximumJsonDepth} levels.`);
  if (value === null || typeof value === "string" || typeof value === "boolean") return;
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new RangeError("A JSON number must be finite.");
    return;
  }
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      if (value[index] === undefined) throw new TypeError("A JSON array cannot hold undefined.");
      checkJson(value[index], depth + 1);
    }
    return;
  }
  if (typeof value === "object") {
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) throw new TypeError("A JSON object must be a plain object.");
    // JSON.stringify leaves out members that are undefined, as for an optional member.
    for (const item of Object.values(value)) if (item !== undefined) checkJson(item, depth + 1);
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
