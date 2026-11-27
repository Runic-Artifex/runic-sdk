import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { test } from "node:test";
import { decodeValue as decode } from "./support/conformance-client.js";

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
