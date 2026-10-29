import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { test } from "node:test";
import * as bridgeWire from "../dist/wire.js";

// The value conformance vectors of specs/application/conformance/vectors/values. Each case decodes one input as a
// wire value type; a valid input's normal form equals `expect.value` (or `expect.text`, read as JSON), and an invalid
// input throws a TypeError or RangeError. A decoded value that is decoded again gives the same normal form.

type Vector = {
  id: string;
  type: string;
  parameters?: { names?: string[]; nullable?: boolean } | null;
  input: { encoding: "json" | "utf8"; value: unknown };
  expect: { value?: unknown; text?: string; error?: { code: string } };
};

const directory = new URL("../../../../specs/application/conformance/vectors/values/", import.meta.url);

const integerTypes: Record<string, { minimum: number; maximum: number }> = {
  int8: { minimum: -128, maximum: 127 },
  uint8: { minimum: 0, maximum: 255 },
  int16: { minimum: -32768, maximum: 32767 },
  uint16: { minimum: 0, maximum: 65535 },
  int32: { minimum: -2147483648, maximum: 2147483647 },
  uint32: { minimum: 0, maximum: 4294967295 },
};

function decode(type: string, parameters: Vector["parameters"], value: unknown): unknown {
  if (value === null && parameters?.nullable === true) return null;
  const range = integerTypes[type];
  if (range !== undefined) return bridgeWire.integer(value, range.minimum, range.maximum);
  switch (type) {
    case "boolean": return bridgeWire.boolean(value);
    case "string": return bridgeWire.string(value);
    case "float64": return bridgeWire.finiteNumber(value);
    case "float32": return bridgeWire.float32(value);
    case "int64": return bridgeWire.int64String(value);
    case "uint64": return bridgeWire.bigint(value, "0", "18446744073709551615").toString();
    case "big-integer": return bridgeWire.bigint(value).toString();
    case "decimal": return bridgeWire.decimal(value);
    case "uuid": return bridgeWire.uuid(value);
    case "local-date": return bridgeWire.localDate(value);
    case "local-time": return bridgeWire.localTime(value);
    case "date-time": return bridgeWire.dateTime(value);
    case "offset-date-time": return bridgeWire.offsetDateTime(value);
    case "duration": return bridgeWire.duration(value);
    case "enum": return bridgeWire.enumName(value, parameters?.names);
    case "json": return parameters?.nullable === false ? bridgeWire.json(value, false) : bridgeWire.json(value);
    default: throw new Error(`No decoder for the wire value type ${type}.`);
  }
}

function check(vector: Vector): void {
  const input = vector.input.encoding === "utf8" ? JSON.parse(vector.input.value as string) : vector.input.value;
  if ("error" in vector.expect) {
    assert.throws(() => decode(vector.type, vector.parameters, input), error => error instanceof TypeError || error instanceof RangeError);
    return;
  }
  const normal = decode(vector.type, vector.parameters, input);
  const expected = vector.expect.text !== undefined ? JSON.parse(vector.expect.text) : vector.expect.value;
  assert.deepEqual(normal, expected);
  assert.deepEqual(decode(vector.type, vector.parameters, normal), normal);
}

for (const file of readdirSync(directory).filter(name => name.endsWith(".json")).sort()) {
  const document = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as { cases: Vector[] };
  for (const vector of document.cases) test(vector.id, () => check(vector));
}
