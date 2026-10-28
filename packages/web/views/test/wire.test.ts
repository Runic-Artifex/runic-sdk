import assert from "node:assert/strict";
import { test } from "node:test";
import * as bridgeWire from "../dist/wire.js";

test("json accepts any JSON value and returns it unchanged", () => {
  const value = { name: "x", items: [1, -2.5, true, null, "s", { nested: [] }], empty: {} };
  assert.equal(bridgeWire.json(value), value);
  for (const scalar of [null, 0, "", false, "text", 1e300]) assert.equal(bridgeWire.json(scalar), scalar);
  const record = Object.assign(Object.create(null) as Record<string, unknown>, { a: 1 });
  assert.equal(bridgeWire.json(record), record);
  // JSON.stringify leaves out an undefined member, as it does for an optional one.
  assert.deepEqual(bridgeWire.json({ a: 1, b: undefined }), { a: 1, b: undefined });
});

test("json rejects values that JSON would change or lose", () => {
  for (const value of [undefined, Number.NaN, Number.POSITIVE_INFINITY, 1n, () => 0, Symbol("s"), new Date(0), new Map(),
    [undefined], [1, Number.NaN], { nested: { value: 1n } }]) {
    assert.throws(() => bridgeWire.json(value), `${String(typeof value)} was accepted`);
  }
  // A value is at most 64 containers deep, so 64 nested arrays around a scalar are valid and 65 are not.
  let deep: unknown = 0;
  for (let depth = 0; depth < 64; depth++) deep = [deep];
  assert.equal(bridgeWire.json(deep), deep);
  deep = [deep];
  assert.throws(() => bridgeWire.json(deep), RangeError);
  const cyclic: Record<string, unknown> = {};
  cyclic["self"] = cyclic;
  assert.throws(() => bridgeWire.json(cyclic), RangeError);
});

test("json rejects null where the C# type cannot hold it", () => {
  assert.throws(() => bridgeWire.json(null, false), TypeError);
  assert.deepEqual(bridgeWire.json({ value: null }, false), { value: null });
  let depth63: unknown = 0;
  for (let depth = 0; depth < 63; depth++) depth63 = [depth63];
  assert.equal(bridgeWire.json(depth63, false), depth63);
});
