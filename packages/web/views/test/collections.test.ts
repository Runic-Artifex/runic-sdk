import assert from "node:assert/strict";
import { test } from "node:test";
import { collectionViewport, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { connectView, defineCollection, defineCollections } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { applyCollectionDelta } from "../dist/collections.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
const decode = (wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 10000), label: bridgeWire.string(row["label"]) };
};
const definitions = { rows: defineCollection(decode, row => String(row.id)) };
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
  ], definitions) as typeof original;
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
    assert.throws(() => applyCollectionDelta(original, [change("add", 1, ["2"], [{ id: 2, label: "two" }]), invalid], definitions));
    assert.deepEqual(original, { rows: [{ id: 1, label: "one" }] });
  }
});

test("clients ignore duplicate deltas and recover a revision gap with one snapshot", async () => {
  const host = globalThis as unknown as Record<string | symbol, unknown>;
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("rows", { state: { rows: [{ id: 1, label: "one" }], title: "rows" } });
  const client = await connectView({ contract: "rows:collections", route: "rows", mount: false, collections: defineCollections(definitions),
    hydrate: (wire: { rows: unknown[]; title: string }) => ({ rows: wire.rows.map(decode), title: wire.title }) });
  const original = client.snapshot.rows[0];
  const push = host["__rowsChanged"] as (wire: unknown) => void;
  const delta = { __runicDelta: 1, baseRevision: 1, revision: 2, changes: [change("add", 1, ["2"], [{ id: 2, label: "two" }])] };
  push(delta); push(delta);
  assert.equal(client.snapshot.rows.length, 2);
  assert.equal(client.snapshot.rows[0], original);
  let recoveries = 0;
  bridge.route("rowsSnapshot", () => { recoveries++; return JSON.stringify({ ok: true, state: { revision: 5, rows: [{ id: 4, label: "recovered" }], title: "rows" }, error: null }); });
  push({ ...delta, baseRevision: 3, revision: 4 });
  // Held during the read and then judged against the recovered revision.
  push({ ...delta, baseRevision: 3, revision: 5 });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(recoveries, 1);
  assert.deepEqual(client.snapshot.rows, [{ id: 4, label: "recovered" }]);
  client.dispose();
});

type Wire = { rows: unknown[]; title: string };
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

/** Connects rows at revision 1 with one row; later snapshot reads use `read`. */
async function connectRows(read: (attempt: number) => unknown) {
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  let reads = 0;
  bridge.route("rowsSnapshot", () => {
    const state = reads++ === 0 ? { revision: 1, rows: [{ id: 1, label: "one" }], title: "rows" } : read(reads - 1);
    return JSON.stringify({ ok: true, state, error: null });
  });
  const client = await connectView({ contract: "rows:recovery", route: "rows", mount: false, collections: defineCollections(definitions),
    hydrate: (wire: Wire) => ({ rows: wire.rows.map(decode), title: wire.title }) });
  const push = host["__rowsChanged"] as (wire: unknown) => void;
  return { bridge, client, push, reads: () => reads - 1 };
}

/** Captures diagnostics and page error reports during `run`. */
async function observe(run: () => Promise<void>) {
  const diagnostics: BridgeDiagnostic[] = [];
  const reported: unknown[] = [];
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  target.reportError = error => { reported.push(error); };
  const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
  try { await run(); } finally { stop(); target.reportError = previous; }
  return { diagnostics, reported };
}

const gap = { __runicDelta: 1, baseRevision: 2, revision: 3, changes: [change("add", 1, ["3"], [{ id: 3, label: "three" }])] };
const recovered = { revision: 3, rows: [{ id: 1, label: "one" }, { id: 3, label: "three" }], title: "rows" };

test("recovery retries a failed snapshot read and reports the failure as a diagnostic", async () => {
  let rows: Awaited<ReturnType<typeof connectRows>> | undefined;
  const { diagnostics, reported } = await observe(async () => {
    rows = await connectRows(attempt => { if (attempt === 1) throw new Error("busy"); return recovered; });
    rows.push(gap);
    for (let waited = 0; rows.reads() < 2 && waited < 2000; waited += 10) await tick(10);
    await tick();
  });
  assert.equal(rows!.reads(), 2);
  assert.deepEqual(rows!.client.snapshot.rows.map(row => row.id), [1, 3]);
  assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.code, diagnostic.route]), [["failed", "rowsSnapshot"]]);
  assert.equal(reported.length, 0);
  rows!.client.dispose();
});

test("recovery gives up after four failed reads and reports the last failure", async () => {
  let rows: Awaited<ReturnType<typeof connectRows>> | undefined;
  const { diagnostics, reported } = await observe(async () => {
    rows = await connectRows(() => { throw new Error("busy"); });
    rows.push(gap);
    await tick(2200);
  });
  assert.equal(rows!.reads(), 4);
  assert.equal(diagnostics.length, 4);
  assert.equal(reported.length, 1);
  assert.equal(rows!.client.snapshot.rows.length, 1);
  // The next frame that needs a snapshot recovers again.
  rows!.push(gap);
  await tick();
  assert.equal(rows!.reads(), 5);
  rows!.client.dispose();
});

test("recovery stops retrying after dispose or disconnect", async () => {
  const disposed = await connectRows(() => { throw new Error("busy"); });
  const disconnected = await connectRows(() => { throw new Error("busy"); });
  await observe(async () => {
    disposed.push(gap);
    await tick();
    disposed.client.dispose();
    disconnected.bridge.disconnect();
    disconnected.push(gap);
    await tick(400);
  });
  assert.equal(disposed.reads(), 1);
  assert.equal(disconnected.reads(), 0);
  disconnected.client.dispose();
});

test("frames that arrive during recovery are applied after it, or recover again", async () => {
  let release: (() => void) | undefined;
  const { bridge, client, push, reads } = await connectRows(() => recovered);
  bridge.route("rowsSnapshot", async () => {
    await new Promise<void>(resolve => { release = resolve; });
    return JSON.stringify({ ok: true, state: recovered, error: null });
  });
  push(gap);
  push({ __runicDelta: 1, baseRevision: 3, revision: 4, changes: [change("add", 2, ["4"], [{ id: 4, label: "four" }])] });
  await tick();
  release!();
  await tick();
  assert.deepEqual(client.snapshot.rows.map(row => row.id), [1, 3, 4]);
  // A held frame that does not continue the recovered state needs another read.
  let second = 0;
  bridge.route("rowsSnapshot", async () => {
    second++;
    if (second === 1) await new Promise<void>(resolve => { release = resolve; });
    return JSON.stringify({ ok: true, state: { ...recovered, revision: second === 1 ? 6 : 9 }, error: null });
  });
  push({ ...gap, baseRevision: 5, revision: 6 });
  push({ ...gap, baseRevision: 7, revision: 8 });
  await tick();
  release!();
  await tick();
  await tick();
  assert.equal(second, 2);
  assert.equal(reads(), 0);
  client.dispose();
});

test("a repeated full state notifies only when its content changed", async () => {
  const { client, push } = await connectRows(() => recovered);
  let notifications = 0;
  client.subscribe(() => { notifications++; });
  notifications = 0;
  const state = { revision: 2, rows: [{ id: 1, label: "one" }], title: "rows" };
  push(state); push(JSON.parse(JSON.stringify(state)));
  assert.equal(notifications, 1);
  // The same revision with other content, as after a change without a notification.
  push({ ...state, title: "renamed" });
  assert.equal(notifications, 2);
  assert.equal(client.snapshot.title, "renamed");
  // A full state that matches the state after a frame, such as a command reply.
  push({ __runicDelta: 1, baseRevision: 2, revision: 3, changes: [change("add", 1, ["3"], [{ id: 3, label: "three" }])] });
  push({ ...recovered, title: "renamed" });
  assert.equal(notifications, 3);
  client.dispose();
});
