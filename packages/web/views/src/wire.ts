// Validating decoders used by generated codecs, published as
// `@runic-artifex/views/generated/wire`. Every graph-backed field stays
// unknown until one of these checks and converts it (for example Int64 strings
// to bigint), so a malformed reply never reaches application state.
//
// Each decoder is its own export. Generated code imports this module as a
// namespace (`import * as bridgeWire`), so a bundler keeps only the decoders a
// module calls.

function decodeBoolean(value: unknown): boolean {
  if (typeof value !== "boolean") throw new TypeError("Expected a boolean.");
  return value;
}

export function integer(value: unknown, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError("Expected a bounded integer.");
  return value;
}

export function finiteNumber(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new RangeError("Expected a finite number.");
  return value;
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

export function guid(value: unknown): string {
  const text = decodeString(value);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) throw new TypeError("Expected a GUID.");
  return text.toLowerCase();
}

export function dateOnly(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) throw new TypeError("Expected an ISO date.");
  return text;
}

export function timeOnly(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?$/.test(text)) throw new TypeError("Expected an ISO time.");
  return text;
}

export function dateTime(value: unknown): string {
  const text = decodeString(value);
  if (!/^\d{4}-\d{2}-\d{2}T/.test(text)) throw new TypeError("Expected an ISO date-time.");
  return text;
}

export function dateTimeOffset(value: unknown): string {
  const text = dateTime(value);
  if (!/(Z|[+-]\d{2}:\d{2})$/.test(text)) throw new TypeError("Expected an ISO offset date-time.");
  return text;
}

export function duration(value: unknown): string {
  const text = decodeString(value);
  const match = /^(-)?(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(text);
  if (!match) throw new TypeError("Expected an invariant time span.");
  const days = BigInt(match[2] ?? "0");
  const hours = BigInt(match[3]!);
  const minutes = BigInt(match[4]!);
  const seconds = BigInt(match[5]!);
  if (hours > 23n || minutes > 59n || seconds > 59n) throw new RangeError("Time span component is out of range.");
  const fraction = BigInt((match[6] ?? "").padEnd(7, "0") || "0");
  const ticks = (((days * 24n + hours) * 60n + minutes) * 60n + seconds) * 10000000n + fraction;
  const signed = match[1] ? -ticks : ticks;
  if (signed < -9223372036854775808n || signed > 9223372036854775807n) throw new RangeError("Time span is out of range.");
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
