import assert from "node:assert/strict";
import { test } from "node:test";
import { collectionViewport, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { connectView, defineCollection, defineCollections, defineText } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge, type MockView } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
const decode = (wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 10000), label: bridgeWire.string(row["label"]) };
};
const definitions = { rows: defineCollection(decode, row => String(row.id)) };
const collections = defineCollections(definitions);
const change = (kind: string, index: number, keys: string[], items: Row[] = [], oldIndex = kind === "add" ? -1 : index) =>
  ({ field: "rows", kind, index, oldIndex, keys, items });

/** Applies a frame's changes of one field as the client does: the first change copies, later ones edit the copy. */
function applyAll(current: unknown, changes: readonly Readonly<Record<string, unknown>>[]): unknown {
  let owned = false;
  for (const next of changes) {
    current = collections.apply(next["field"] as string, current, next, owned);
    owned = true;
  }
  collections.validate("rows", current);
  return current;
}

test("viewports bound rows and clamp the start and end of large sources", () => {
  assert.deepEqual(collectionViewport({ totalCount: 100000, scrollTop: 32000, height: 640, rowHeight: 32 }),
    { start: 995, size: 30, offset: 31840, totalSize: 3200000 });
  assert.deepEqual(collectionViewport({ totalCount: 3, scrollTop: -20, height: 640, rowHeight: 32 }),
    { start: 0, size: 3, offset: 0, totalSize: 96 });
  assert.equal(collectionViewport({ totalCount: 0, scrollTop: 0, height: 640, rowHeight: 32 }).size, 0);
  assert.throws(() => collectionViewport({ totalCount: 2, scrollTop: 0, height: 100, rowHeight: 0 }), RangeError);
});

test("defineCollections names keyed collections and text fields, and validates their values", () => {
  const both = defineCollections({ ...definitions, log: defineText(true) });
  assert.deepEqual(["rows", "log", "title", "__proto__"].map(field => both.kind(field)), ["collection", "text", undefined, undefined]);
  both.validate("log", null);
  both.validate("title", 7);
  assert.throws(() => both.validate("rows", [{ id: 1, label: "one" }, { id: 1, label: "again" }]), /unique/);
  assert.throws(() => both.validate("rows", {}), /not an array/);
  assert.throws(() => defineCollections({ log: defineText() }).validate("log", null), /not a string/);
  assert.throws(() => both.apply("rows", [], { field: "rows", kind: "text", length: 0, trim: 0, text: "x" }, false), /text change/);
  assert.equal(both.apply("log", "abc", { field: "log", kind: "text", length: 3, trim: 1, text: "d" }, false), "bcd");
});

test("collection changes preserve identity and apply indexed operations in order", () => {
  const original = [decode({ id: 1, label: "one" }), decode({ id: 2, label: "two" })];
  const next = applyAll(original, [
    change("replace", 1, ["2"], [{ id: 2, label: "new two" }]),
    change("add", 2, ["3"], [{ id: 3, label: "three" }]),
    change("move", 0, ["3"], [], 2),
    change("remove", 2, ["2"]),
  ]) as Row[];
  assert.deepEqual(next.map(row => row.id), [3, 1]);
  assert.equal(next[1], original[0]);
  assert.deepEqual(original.map(row => row.id), [1, 2]);
});

test("invalid changes leave the previous collection untouched", () => {
  const original = [{ id: 1, label: "one" }];
  for (const invalid of [
    change("remove", 0, ["wrong"]), change("add", 1, ["1"], [{ id: 1, label: "duplicate" }]),
    change("replace", 0, ["1"], [{ id: 2, label: 7 as unknown as string }]),
    change("move", 2, ["1"], [], 0), change("add", 1, ["9"], [{ id: 3, label: "three" }]), change("add", 5, ["4"], [{ id: 4, label: "four" }]),
    { ...change("remove", 0, ["1"]), field: "__proto__" }, { ...change("remove", 0, ["1"]), kind: "splice" },
  ]) {
    assert.throws(() => applyAll(original, [change("add", 1, ["2"], [{ id: 2, label: "two" }]), invalid]), JSON.stringify(invalid));
    assert.deepEqual(original, [{ id: 1, label: "one" }]);
  }
});

type Wire = { readonly rows: readonly Row[]; readonly title: string };
const members = { rows: (wire: unknown) => bridgeWire.array(wire, decode), title: bridgeWire.string };
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));
const initial = { rows: [{ id: 1, label: "one" }], title: "rows" };

/** Connects `rows` at revision 0 with one row; recovery reads are answered by `read` (a throw drops the transport). */
async function connectRows(read: (attempt: number) => { readonly revision: number; readonly state: Wire } | Promise<{ readonly revision: number; readonly state: Wire }>) {
  const bridge = installMockBridge(createMockBridge());
  const view = bridge.view("rows", { state: initial, collections: { rows: item => String((item as Row).id) } });
  let reads = 0;
  bridge.intercept(async ({ kind, route }) => kind === "snapshot" && route === "rows" ? { ok: true, ...await read(++reads) } : undefined);
  const client = await connectView<Wire>({ contract: "rows:recovery", route: "rows", members, collections });
  const push = (frame: Record<string, unknown>) => view.push({ kind: "frame", ...frame });
  return { bridge, view, client, push, reads: () => reads };
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

const gap = { baseRevision: 2, revision: 3, changes: [change("add", 1, ["3"], [{ id: 3, label: "three" }])] };
const recovered = { revision: 3, state: { rows: [{ id: 1, label: "one" }, { id: 3, label: "three" }], title: "rows" } };

test("clients ignore a repeated frame and recover a revision gap with one snapshot", async () => {
  const snapshot = { revision: 5, state: { rows: [{ id: 4, label: "recovered" }], title: "rows" } };
  const { client, push, reads } = await connectRows(() => snapshot);
  const original = client.snapshot.rows[0];
  const frame = { baseRevision: 0, revision: 2, changes: [change("add", 1, ["2"], [{ id: 2, label: "two" }])] };
  push(frame); push(frame);
  assert.equal(client.snapshot.rows.length, 2);
  assert.equal(client.snapshot.rows[0], original);
  assert.equal(reads(), 0);
  push({ ...frame, baseRevision: 3, revision: 4 });
  // Held during the read and then judged against the recovered revision.
  push({ ...frame, baseRevision: 3, revision: 5 });
  await tick();
  assert.equal(reads(), 1);
  assert.deepEqual(client.snapshot.rows, [{ id: 4, label: "recovered" }]);
  client.dispose();
});

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
  assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.code, diagnostic.route]), [["failed", "rows"]]);
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
  await observe(async () => {
    rows!.push(gap);
    await tick();
  });
  assert.equal(rows!.reads(), 5);
  rows!.client.dispose();
});

test("recovery stops after dispose silently, and after a disconnect with a report", async () => {
  const disposed = await connectRows(() => { throw new Error("busy"); });
  const disposedRun = await observe(async () => {
    disposed.push(gap);
    await tick();
    disposed.client.dispose();
    await tick(400);
  });
  assert.equal(disposed.reads(), 1);
  assert.equal(disposedRun.reported.length, 0);

  const disconnected = await connectRows(() => { throw new Error("busy"); });
  const disconnectedRun = await observe(async () => {
    disconnected.push(gap);
    await tick();
    disconnected.bridge.disconnect();
    await tick(400);
  });
  assert.equal(disconnected.reads(), 1);
  assert.deepEqual(disconnectedRun.reported.map(error => (error as { kind?: string }).kind), ["disconnected"]);
  disconnected.client.dispose();
});

test("frames that arrive during recovery are applied after it", async () => {
  const gates: (() => void)[] = [];
  const { client, push, reads } = await connectRows(async () => {
    await new Promise<void>(resolve => { gates.push(resolve); });
    return recovered;
  });
  push(gap);
  push({ baseRevision: 3, revision: 4, changes: [change("add", 2, ["4"], [{ id: 4, label: "four" }])] });
  // A frame at or below the recovered revision is ignored.
  push({ ...gap, baseRevision: 2, revision: 3 });
  await tick();
  gates.shift()!();
  await tick();
  assert.equal(reads(), 1);
  assert.deepEqual(client.snapshot.rows.map(row => row.id), [1, 3, 4]);
  client.dispose();
});

test("a frame held during recovery that does not continue the recovered state starts another recovery", async () => {
  const gates: (() => void)[] = [];
  const answers = [{ ...recovered, revision: 6 }, { ...recovered, revision: 9 }];
  const { client, push, reads } = await connectRows(async attempt => {
    if (attempt === 1) await new Promise<void>(resolve => { gates.push(resolve); });
    return answers[attempt - 1]!;
  });
  push({ ...gap, baseRevision: 5, revision: 6 });
  push({ ...gap, baseRevision: 7, revision: 8 });
  await tick();
  assert.equal(reads(), 1);
  gates.shift()!();
  await tick(50);
  assert.equal(reads(), 2);
  assert.deepEqual(client.snapshot.rows.map(row => row.id), [1, 3]);
  client.dispose();
});

test("a state or frame at a revision the client already has is ignored and does not notify", async () => {
  const { client, view, push } = await connectRows(() => recovered);
  let notifications = 0;
  client.subscribe(() => { notifications++; });
  notifications = 0;
  const state = { kind: "state", revision: 2, state: initial };
  view.push(state); view.push(JSON.parse(JSON.stringify(state)) as typeof state);
  assert.equal(notifications, 1);
  // Two states of a route at one revision are equal, so another content at the same revision is not applied.
  view.push({ ...state, state: { ...initial, title: "renamed" } });
  assert.equal(notifications, 1);
  assert.equal(client.snapshot.title, "rows");
  push({ baseRevision: 1, revision: 2, changes: [{ field: "title", kind: "set", value: "renamed" }] });
  assert.equal(client.snapshot.title, "rows");
  push({ baseRevision: 2, revision: 3, changes: [{ field: "title", kind: "set", value: "renamed" }] });
  assert.equal(notifications, 2);
  assert.equal(client.snapshot.title, "renamed");
  client.dispose();
});

test("the mock's collection edits reach the client as indexed changes", async () => {
  const bridge: MockBridge = installMockBridge(createMockBridge());
  const view: MockView = bridge.view("rows", { state: initial, collections: { rows: item => String((item as Row).id) } });
  const client = await connectView<Wire>({ contract: "rows:mock", route: "rows", members, collections });
  const first = client.snapshot.rows[0];
  view.batch(() => {
    view.collection("rows").add([{ id: 2, label: "two" }, { id: 3, label: "three" }], 0);
    view.collection("rows").move("1", 0);
    view.collection("rows").replace("3", { id: 3, label: "THREE" });
    view.update({ title: "edited" });
  });
  view.collection("rows").remove("2");
  assert.deepEqual(client.snapshot, { rows: [{ id: 1, label: "one" }, { id: 3, label: "THREE" }], title: "edited" });
  assert.equal(client.snapshot.rows[0], first);
  assert.throws(() => view.collection("rows").add({ id: 1, label: "again" }), RangeError);
  assert.throws(() => view.collection("title"), RangeError);
  assert.equal(bridge.calls.filter(call => call.kind === "snapshot").length, 0);
  client.dispose();
});

test("a frame checks the keys of the rows it changes, not of the whole collection", () => {
  let keyCalls = 0;
  const counted = defineCollections({ rows: defineCollection(decode, row => { keyCalls++; return String(row.id); }) });
  const rows = Array.from({ length: 10000 }, (_, id) => ({ id, label: `row ${id}` }));
  counted.validate("rows", rows);
  assert.equal(keyCalls, 10000);
  keyCalls = 0;
  let current: unknown = rows;
  for (let at = 0; at < 50; at++) {
    current = counted.apply("rows", current, change("replace", at, [String(at)], [{ id: at, label: "edited" }]), false);
    counted.validate("rows", current);
  }
  current = counted.apply("rows", current, change("add", 0, ["10000"], [{ id: 10000, label: "added" }]), false);
  current = counted.apply("rows", current, change("remove", 1, ["0"]), true);
  counted.validate("rows", current);
  assert.ok(keyCalls < 200, `${keyCalls} key calls`);
  assert.equal((current as Row[]).length, 10000);
  assert.equal(rows[0]!.label, "row 0");
});

test("the key index rejects duplicates a change introduces and survives a failed frame", () => {
  const rows = [{ id: 1, label: "one" }, { id: 2, label: "two" }];
  collections.validate("rows", rows);
  assert.throws(() => collections.apply("rows", rows, change("add", 2, ["1"], [{ id: 1, label: "again" }]), false), /unique/);
  assert.throws(() => collections.apply("rows", rows, change("replace", 0, ["1"], [{ id: 2, label: "two again" }]), false), /unique/);
  assert.throws(() => collections.apply("rows", rows, change("add", 0, ["3", "3"], [{ id: 3, label: "a" }, { id: 3, label: "b" }]), false), /unique/);
  // The previous value is still usable after the failed changes, and its keys are still known.
  const next = collections.apply("rows", rows, change("replace", 0, ["1"], [{ id: 3, label: "three" }]), false) as Row[];
  collections.validate("rows", next);
  assert.deepEqual(next.map(row => row.id), [3, 2]);
  assert.throws(() => collections.apply("rows", next, change("add", 0, ["2"], [{ id: 2, label: "dup" }]), false), /unique/);
  const again = collections.apply("rows", next, change("add", 0, ["1"], [{ id: 1, label: "one" }]), false) as Row[];
  collections.validate("rows", again);
  assert.deepEqual(again.map(row => row.id), [1, 3, 2]);
  assert.deepEqual(rows.map(row => row.id), [1, 2]);
  collections.validate("rows", rows);
});

test("changes of more rows than an engine accepts as arguments apply in parts", () => {
  type Big = { readonly id: number };
  const big = defineCollections({ rows: defineCollection(wire => ({ id: (wire as Big).id }), (row: Big) => String(row.id)) });
  const count = 150000;
  const added = Array.from({ length: count }, (_, offset) => ({ id: 1000 + offset }));
  const keys = added.map(row => String(row.id));
  const start = [{ id: 1 }, { id: 2 }, { id: 3 }];
  big.validate("rows", start);
  let rows = big.apply("rows", start, { field: "rows", kind: "add", index: 1, oldIndex: -1, keys, items: added }, false) as Big[];
  big.validate("rows", rows);
  assert.equal(rows.length, count + 3);
  assert.deepEqual([rows[0], rows[1], rows[count], rows[count + 1], rows[count + 2]].map(row => row!.id), [1, 1000, 1000 + count - 1, 2, 3]);
  rows = big.apply("rows", rows, { field: "rows", kind: "move", index: 2, oldIndex: 1, keys, items: [] }, false) as Big[];
  assert.deepEqual([rows[0], rows[1], rows[2], rows[count + 2]].map(row => row!.id), [1, 2, 1000, 3]);
  const replaced = added.map(row => ({ id: row.id + count }));
  rows = big.apply("rows", rows, { field: "rows", kind: "replace", index: 2, oldIndex: 2, keys, items: replaced }, false) as Big[];
  big.validate("rows", rows);
  assert.deepEqual([rows[1], rows[2], rows[count + 1], rows[count + 2]].map(row => row!.id), [2, 1000 + count, 1000 + 2 * count - 1, 3]);
});
