using System.Text;

// Shared frontend validation runtime used by BridgeTypeGraph decoder snippets.
// It is emitted into each generated module, so it has no frontend framework or
// runtime package dependency.
internal static class BridgeTypeScriptWireEmitter
{
    internal static void AppendRuntime(StringBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.AppendLine("const bridgeWire = {");
        source.AppendLine("  boolean(value: unknown): boolean { if (typeof value !== \"boolean\") throw new TypeError(\"Expected a boolean.\"); return value; },");
        source.AppendLine("  integer(value: unknown, minimum: number, maximum: number): number { if (typeof value !== \"number\" || !Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError(\"Expected a bounded integer.\"); return value; },");
        source.AppendLine("  finiteNumber(value: unknown): number { if (typeof value !== \"number\" || !Number.isFinite(value)) throw new RangeError(\"Expected a finite number.\"); return value; },");
        source.AppendLine("  string(value: unknown): string { if (typeof value !== \"string\") throw new TypeError(\"Expected a string.\"); return value; },");
        source.AppendLine("  bigint(value: unknown, minimum?: string, maximum?: string): bigint { if (typeof value !== \"string\" || !/^-?(0|[1-9][0-9]*)$/.test(value)) throw new TypeError(\"Expected an integer string.\"); const result = BigInt(value); if (minimum !== undefined && result < BigInt(minimum)) throw new RangeError(\"Integer is below range.\"); if (maximum !== undefined && result > BigInt(maximum)) throw new RangeError(\"Integer is above range.\"); return result; },");
        source.AppendLine("  int64String(value: unknown): string { this.bigint(value, \"-9223372036854775808\", \"9223372036854775807\"); return value as string; },");
        source.AppendLine("  decimal(value: unknown): string { if (typeof value !== \"string\" || !/^-?(0|[1-9][0-9]*)(\\.[0-9]+)?$/.test(value)) throw new TypeError(\"Expected a decimal string.\"); return value; },");
        source.AppendLine("  guid(value: unknown): string { const text = this.string(value); if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) throw new TypeError(\"Expected a GUID.\"); return text.toLowerCase(); },");
        source.AppendLine("  dateOnly(value: unknown): string { const text = this.string(value); if (!/^\\d{4}-\\d{2}-\\d{2}$/.test(text)) throw new TypeError(\"Expected an ISO date.\"); return text; },");
        source.AppendLine("  timeOnly(value: unknown): string { const text = this.string(value); if (!/^\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?$/.test(text)) throw new TypeError(\"Expected an ISO time.\"); return text; },");
        source.AppendLine("  dateTime(value: unknown): string { const text = this.string(value); if (!/^\\d{4}-\\d{2}-\\d{2}T/.test(text)) throw new TypeError(\"Expected an ISO date-time.\"); return text; },");
        source.AppendLine("  dateTimeOffset(value: unknown): string { const text = this.dateTime(value); if (!/(Z|[+-]\\d{2}:\\d{2})$/.test(text)) throw new TypeError(\"Expected an ISO offset date-time.\"); return text; },");
        source.AppendLine("  enumName(value: unknown, names?: readonly string[]): string { const text = this.string(value); if (names !== undefined && !names.includes(text)) throw new RangeError(\"Unknown enum name.\"); return text; },");
        source.AppendLine("  array<T>(value: unknown, decode: (item: unknown) => T): readonly T[] { if (!Array.isArray(value)) throw new TypeError(\"Expected an array.\"); return value.map(decode); },");
        source.AppendLine("  stringRecord<T>(value: unknown, decode: (item: unknown) => T): Readonly<Record<string, T>> { const object = this.object(value, item => item); const result: Record<string, T> = {}; for (const [key, item] of Object.entries(object)) result[key] = decode(item); return result; },");
        source.AppendLine("  object<T>(value: unknown, decode: (item: Record<string, unknown>) => T): T { if (value === null || typeof value !== \"object\" || Array.isArray(value)) throw new TypeError(\"Expected an object.\"); return decode(value as Record<string, unknown>); },");
        source.AppendLine("  union(value: unknown): any { const object = this.object(value, item => item); if (typeof object.$case !== \"string\") throw new TypeError(\"Expected a union discriminator.\"); return object; },");
        source.AppendLine("  encodeUnion(value: unknown): Record<string, unknown> { const object = this.object(value, item => item); if (typeof object.$case !== \"string\") throw new TypeError(\"Expected a union discriminator.\"); return object; },");
        source.AppendLine("};");
        source.AppendLine();
    }
}
