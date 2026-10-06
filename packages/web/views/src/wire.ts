// Validating decoders used by generated codecs. Every graph-backed field stays
// unknown until one of these checks and converts it (for example Int64 strings
// to bigint), so a malformed reply never reaches application state.
export const bridgeWire = {
  boolean(value: unknown): boolean { if (typeof value !== "boolean") throw new TypeError("Expected a boolean."); return value; },
  integer(value: unknown, minimum: number, maximum: number): number { if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError("Expected a bounded integer."); return value; },
  finiteNumber(value: unknown): number { if (typeof value !== "number" || !Number.isFinite(value)) throw new RangeError("Expected a finite number."); return value; },
  string(value: unknown): string { if (typeof value !== "string") throw new TypeError("Expected a string."); return value; },
  bigint(value: unknown, minimum?: string, maximum?: string): bigint { if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)$/.test(value)) throw new TypeError("Expected an integer string."); const result = BigInt(value); if (minimum !== undefined && result < BigInt(minimum)) throw new RangeError("Integer is below range."); if (maximum !== undefined && result > BigInt(maximum)) throw new RangeError("Integer is above range."); return result; },
  int64String(value: unknown): string { this.bigint(value, "-9223372036854775808", "9223372036854775807"); return value as string; },
  decimal(value: unknown): string { if (typeof value !== "string" || !/^-?(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(value)) throw new TypeError("Expected a decimal string."); return value; },
  guid(value: unknown): string { const text = this.string(value); if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) throw new TypeError("Expected a GUID."); return text.toLowerCase(); },
  dateOnly(value: unknown): string { const text = this.string(value); if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) throw new TypeError("Expected an ISO date."); return text; },
  timeOnly(value: unknown): string { const text = this.string(value); if (!/^\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?$/.test(text)) throw new TypeError("Expected an ISO time."); return text; },
  dateTime(value: unknown): string { const text = this.string(value); if (!/^\d{4}-\d{2}-\d{2}T/.test(text)) throw new TypeError("Expected an ISO date-time."); return text; },
  dateTimeOffset(value: unknown): string { const text = this.dateTime(value); if (!/(Z|[+-]\d{2}:\d{2})$/.test(text)) throw new TypeError("Expected an ISO offset date-time."); return text; },
  duration(value: unknown): string { const text = this.string(value); const match = /^(-)?(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(text); if (!match) throw new TypeError("Expected an invariant time span."); const days = BigInt(match[2] ?? "0"); const hours = BigInt(match[3]!); const minutes = BigInt(match[4]!); const seconds = BigInt(match[5]!); if (hours > 23n || minutes > 59n || seconds > 59n) throw new RangeError("Time span component is out of range."); const fraction = BigInt((match[6] ?? "").padEnd(7, "0") || "0"); const ticks = (((days * 24n + hours) * 60n + minutes) * 60n + seconds) * 10000000n + fraction; const signed = match[1] ? -ticks : ticks; if (signed < -9223372036854775808n || signed > 9223372036854775807n) throw new RangeError("Time span is out of range."); return text; },
  enumName<T extends string = string>(value: unknown, names?: readonly T[]): T { const text = this.string(value); if (names !== undefined && !(names as readonly string[]).includes(text)) throw new RangeError("Unknown enum name."); return text as T; },
  array<T>(value: unknown, decode: (item: unknown) => T): readonly T[] { if (!Array.isArray(value)) throw new TypeError("Expected an array."); return value.map(decode); },
  stringRecord<T>(value: unknown, decode: (item: unknown) => T): Readonly<Record<string, T>> { const object = this.object(value, item => item); const result = Object.create(null) as Record<string, T>; for (const [key, item] of Object.entries(object)) result[key] = decode(item); return result; },
  object<T>(value: unknown, decode: (item: Record<string, unknown>) => T): T { if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Expected an object."); return decode(value as Record<string, unknown>); },
  union(value: unknown): any { const object = this.object(value, item => item); if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator."); return object; },
  encodeUnion(value: unknown): Record<string, unknown> { const object = this.object(value, item => item); if (typeof object["$case"] !== "string") throw new TypeError("Expected a union discriminator."); return object; },
};

export interface BridgeValidationMessage {
  readonly path: readonly (string | number)[];
  readonly message: string;
  readonly code?: string;
  readonly severity?: string;
}

export interface BridgeValidationState {
  readonly hasErrors: boolean;
  readonly truncated: boolean;
  readonly errors: readonly BridgeValidationMessage[];
}

export function decodeBridgeValidation(value: unknown): BridgeValidationState {
  return bridgeWire.object(value, state => ({
    hasErrors: bridgeWire.boolean(state["hasErrors"]),
    truncated: bridgeWire.boolean(state["truncated"]),
    errors: bridgeWire.array(state["errors"], value => bridgeWire.object(value, error => ({
      path: bridgeWire.array(error["path"], part => typeof part === "string" ? part : bridgeWire.integer(part, 0, Number.MAX_SAFE_INTEGER)),
      message: bridgeWire.string(error["message"]),
      ...(error["code"] === undefined ? {} : { code: bridgeWire.string(error["code"]) }),
      ...(error["severity"] === undefined ? {} : { severity: bridgeWire.string(error["severity"]) }),
    }))),
  }));
}
