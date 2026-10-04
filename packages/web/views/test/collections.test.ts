import assert from "node:assert/strict";
import { test } from "node:test";
import { bridgeWire, collectionViewport, connectView, defineCollection } from "../dist/index.js";
import { applyCollectionDelta } from "../dist/collections.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
const decode = (wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 10000), label: bridgeWire.string(row["label"]) };
};
const collections = { rows: defineCollection(decode, row => String(row.id)) };
const change = (kind: string, index: number, keys: string[], items: Row[] = [], oldIndex = index) =>
  ({ field: "rows", kind, index, oldIndex, keys, items });

test("viewports bound rows and clamp the start and end of large sources", () => {
  assert.deepEqual(collectionViewport({ totalCount: 100000, scrollTop: 32000, height: 640, rowHeight: 32 }),
    { start: 995, size: 30, offset: 31840, totalSize: 3200000 });
  assert.deepEqual(collectionViewport({ totalCount: 3, scrollTop: -20, height: 640, rowHeight: 32 }),
    { start: 0, size: 3, offset: 0, totalSize: 96 });
  assert.equal(collectionViewport({ totalCount: 0, scrollTop: 0, height: 640, rowHeight: 32 }).size, 0);
  assert.throws(() => collectionViewport({ totalCount: 2, scrollTop: 0, height: 100, rowHeight: 0 }), RangeError);
});

test("collection frames preserve identity and apply indexed operations in order", () => {
  const original = { title: "rows", rows: [decode({ id: 1, label: "one" }), decode({ id: 2, label: "two" })] };
  const next = applyCollectionDelta(original, [
    change("replace", 1, ["2"], [{ id: 2, label: "new two" }]),
    change("add", 2, ["3"], [{ id: 3, label: "three" }]),
    change("move", 0, ["3"], [], 2),
    change("remove", 2, ["2"]),
  ], collections) as typeof original;
  assert.deepEqual(next.rows.map(row => row.id), [3, 1]);
  assert.equal(next.rows[1], original.rows[0]);
  assert.equal(next.title, original.title);
  assert.deepEqual(original.rows.map(row => row.id), [1, 2]);
});

test("invalid frames leave the previous collection untouched", () => {
  const original = { rows: [{ id: 1, label: "one" }] };
  for (const invalid of [
    change("remove", 0, ["wrong"]), change("add", 1, ["1"], [{ id: 1, label: "duplicate" }]),
    change("replace", 0, ["1"], [{ id: 2, label: 7 as unknown as string }]),
    change("move", 2, ["1"], [], 0), { ...change("remove", 0, ["1"]), field: "__proto__" },
  ]) {
    assert.throws(() => applyCollectionDelta(original, [change("add", 1, ["2"], [{ id: 2, label: "two" }]), invalid], collections));
    assert.deepEqual(original, { rows: [{ id: 1, label: "one" }] });
  }
});

test("clients ignore duplicate deltas and recover a revision gap with one snapshot", async () => {
  const host = globalThis as unknown as Record<string | symbol, unknown>;
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("rows", { state: { rows: [{ id: 1, label: "one" }], title: "rows" } });
  const client = await connectView({ contract: "rows:collections", route: "rows", mount: false, collections,
    hydrate: (wire: { rows: unknown[]; title: string }) => ({ rows: wire.rows.map(decode), title: wire.title }) });
  const original = client.snapshot.rows[0];
  const push = host["__rowsChanged"] as (wire: unknown) => void;
  const delta = { __runicDelta: 1, baseRevision: 1, revision: 2, changes: [change("add", 1, ["2"], [{ id: 2, label: "two" }])] };
  push(delta); push(delta);
  assert.equal(client.snapshot.rows.length, 2);
  assert.equal(client.snapshot.rows[0], original);
  let recoveries = 0;
  bridge.route("rowsSnapshot", () => { recoveries++; return JSON.stringify({ ok: true, state: { revision: 4, rows: [{ id: 4, label: "recovered" }], title: "rows" }, error: null }); });
  push({ ...delta, baseRevision: 3, revision: 4 });
  push({ ...delta, baseRevision: 3, revision: 5 });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(recoveries, 1);
  assert.deepEqual(client.snapshot.rows, [{ id: 4, label: "recovered" }]);
  client.dispose();
});
