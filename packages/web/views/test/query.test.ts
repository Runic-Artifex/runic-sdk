import assert from "node:assert/strict";
import { test } from "node:test";
import { createQueryViewportController, createRowSizes, normalizeQuery, type QueryCollectionState } from "../dist/index.js";
import { connectView, decodeQueryCollectionState, defineQueryCollection } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string; readonly size: number };
const decode = (wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 1_000_000), label: bridgeWire.string(row["label"]), size: bridgeWire.integer(row["size"], 0, 1000) };
};
type State = { readonly rows: QueryCollectionState; readonly title: string };
const members = { rows: decodeQueryCollectionState, title: bridgeWire.string };
const rows = (count: number) => Array.from({ length: count }, (_, id) => ({ id, label: `row ${id}`, size: id % 7 }));
const definition = defineQueryCollection(decode);

async function connect(count: number, options: { latency?: number; bridge?: MockBridge } = {}) {
  const bridge = installMockBridge(options.bridge ?? createMockBridge());
  const view = bridge.view("catalog", {
    state: { title: "catalog" },
    queries: { rows: { rows: rows(count), key: row => String((row as Row).id), latency: options.latency ?? 0,
      custom: { even: row => (row as Row).id % 2 === 0 } } },
  });
  const client = await connectView<State>({ contract: "catalog:query", route: "catalog", members });
  const collection = client.queryCollection("rows", definition);
  return { bridge, view, client, collection };
}

const until = async (check: () => boolean, bridge: MockBridge) => {
  for (let round = 0; round < 200 && !check(); round++) await bridge.flush();
  assert.ok(check(), "the condition did not hold");
};

test("a query collection loads only its range, with placeholders until the rows arrive", async () => {
  const { bridge, view, client, collection } = await connect(100_000);
  assert.deepEqual(client.snapshot.rows, { query: { sort: [], filters: [] }, total: 100_000, version: 1 });
  collection.setRange(500, 20);
  assert.equal(collection.current.rows.length, 20);
  assert.ok(collection.current.rows.every(row => row.status === "loading" && row.row === undefined));
  await until(() => collection.current.rows.every(row => row.status === "loaded"), bridge);
  assert.deepEqual(collection.current.rows.map(row => row.row!.id), Array.from({ length: 20 }, (_, index) => 500 + index));
  assert.equal(collection.current.rows[0]!.key, "500");
  assert.deepEqual(view.query("rows").loads, [{ start: 500, count: 20, version: 1 }]);
  // The same range again is already loaded.
  collection.setRange(505, 10);
  await bridge.flush();
  assert.equal(view.query("rows").loads.length, 1);
  client.dispose();
});

test("scrolling coalesces ranges: one request in flight, then only the latest range", async () => {
  const bridge = createMockBridge({ scheduling: "manual" });
  const { view, collection, client } = await (async () => {
    installMockBridge(bridge);
    const pending = connect(10_000, { latency: 50, bridge });
    await bridge.flush();
    return bridge.flushUntil(pending);
  })();
  for (let start = 0; start < 2_000; start += 100) collection.setRange(start, 30);
  await bridge.flush();
  assert.equal(view.query("rows").loads.length, 1);
  await bridge.advance(50);
  await bridge.advance(50);
  await bridge.flush();
  const loads = view.query("rows").loads;
  assert.deepEqual(loads.map(load => load.start), [0, 1900]);
  assert.ok(collection.current.rows.every(row => row.status === "loaded"));
  assert.equal(collection.current.rows[0]!.row!.id, 1900);
  client.dispose();
});

test("setQuery sorts and filters for every presentation, and each keeps its own range", async () => {
  const { bridge, view, client, collection } = await connect(1_000);
  const second = client.queryCollection("rows", definition);
  collection.setRange(0, 5);
  second.setRange(900, 5);
  await until(() => collection.current.rows.every(row => row.status === "loaded") && second.current.rows.every(row => row.status === "loaded"), bridge);
  await collection.setQuery({ sort: [{ field: "size", descending: true }], filters: [{ field: "label", op: "contains", value: "9" }] });
  assert.deepEqual(client.snapshot.rows.query, normalizeQuery({ sort: [{ field: "size", descending: true }], filters: [{ field: "label", op: "contains", value: "9" }] }));
  assert.equal(client.snapshot.rows.version, 2);
  const total = view.query("rows").total;
  assert.equal(client.snapshot.rows.total, total);
  await until(() => collection.current.rows.every(row => row.status === "loaded" && !row.stale), bridge);
  assert.ok(collection.current.rows.every(row => row.row!.size === 6 && row.row!.label.includes("9")));
  // The second presentation's range lies past the filtered total: it shows nothing, and asks for nothing.
  assert.equal(second.current.count, 0);
  await assert.rejects(collection.setQuery({ filters: [{ field: "id", op: "custom", value: null }] }), /not valid/);
  await collection.setQuery({ filters: [{ field: "even", op: "custom" }] });
  await until(() => collection.current.rows.every(row => row.status === "loaded"), bridge);
  assert.deepEqual(collection.current.rows.map(row => row.row!.id), [0, 2, 4, 6, 8]);
  client.dispose();
});

test("a new version reloads the range and keeps the stale rows until then", async () => {
  const { bridge, view, client, collection } = await connect(50);
  collection.setRange(0, 3);
  await until(() => collection.current.rows.every(row => row.status === "loaded"), bridge);
  const source = view.query("rows");
  source.setRows(rows(50).map(row => row.id === 1 ? { ...row, label: "changed" } : row));
  await bridge.flush();
  await until(() => collection.current.rows[1]!.row!.label === "changed" && !collection.current.rows[1]!.stale, bridge);
  assert.equal(source.loads.length, 2);
  assert.equal(source.loads[1]!.version, 2);
  client.dispose();
});

test("indexOf finds a key in the current query, and a failed load is retried on demand", async () => {
  const { bridge, view, client, collection } = await connect(300);
  assert.equal(await collection.indexOf("250"), 250);
  assert.equal(await collection.indexOf("nope"), undefined);
  await collection.setQuery({ sort: [{ field: "id", descending: true }] });
  assert.equal(await collection.indexOf("250"), 49);
  view.failNext({ kind: "query-rows", member: "rows" }, { kind: "failed", message: "database offline" });
  collection.setRange(0, 4);
  await until(() => collection.current.error !== undefined, bridge);
  assert.equal(collection.current.rows[0]!.status, "error");
  assert.match(collection.current.error!.message, /database offline/);
  collection.retry();
  await until(() => collection.current.rows.every(row => row.status === "loaded"), bridge);
  assert.equal(collection.current.error, undefined);
  client.dispose();
});

test("row sizes keep measured heights and find rows by offset in a long list", () => {
  const sizes = createRowSizes(500_000, 20);
  assert.equal(sizes.totalSize, 10_000_000);
  assert.equal(sizes.indexAt(0), 0);
  assert.equal(sizes.indexAt(19.9), 0);
  assert.equal(sizes.indexAt(20), 1);
  assert.equal(sizes.measure(1, 60), 40);
  assert.equal(sizes.offsetOf(2), 80);
  assert.equal(sizes.indexAt(79), 1);
  assert.equal(sizes.indexAt(80), 2);
  assert.equal(sizes.indexAt(1e12), 499_999);
  sizes.resize(3);
  assert.equal(sizes.totalSize, 100);
  sizes.resize(5);
  assert.equal(sizes.sizeOf(1), 60);
  sizes.reset();
  assert.equal(sizes.sizeOf(1), 20);
});

test("a query viewport asks for the rows a container shows and scrolls to a key", async () => {
  const { bridge, client, collection } = await connect(10_000);
  const viewport = createQueryViewportController(collection, { estimateSize: 30, overscan: 2 });
  // The viewport follows the collection while it has subscribers, as a binding's.
  let notified = 0;
  const unsubscribe = viewport.subscribe(() => { notified++; });
  const element = { scrollTop: 0, clientHeight: 300, addEventListener() {}, removeEventListener() {} } as unknown as HTMLElement;
  viewport.attach(element);
  assert.deepEqual([collection.current.start, collection.current.count], [0, 13]);
  element.scrollTop = 3_000;
  viewport.attach(null);
  viewport.attach(element);
  assert.deepEqual([collection.current.start, collection.current.count], [98, 15]);
  await until(() => viewport.current.items.every(item => item.status === "loaded"), bridge);
  assert.equal(viewport.current.items[2]!.top, 3_000);
  // A row above the visible ones that grows keeps them in place.
  viewport.measure(10, 130);
  assert.equal(element.scrollTop, 3_100);
  assert.equal(viewport.current.totalSize, 300_100);
  assert.equal(await viewport.scrollToKey("5000"), true);
  assert.equal(element.scrollTop, 5_000 * 30 + 100);
  assert.equal(collection.current.start, 4_998);
  assert.equal(await viewport.scrollToKey("missing"), false);
  assert.ok(notified > 0);
  unsubscribe();
  viewport.dispose();
  client.dispose();
});

test("query state and queries decode and normalize strictly", () => {
  assert.throws(() => decodeQueryCollectionState({ query: {}, total: -1, version: 0 }), TypeError);
  assert.throws(() => normalizeQuery({ sort: [{ field: "" }] }), RangeError);
  assert.throws(() => normalizeQuery({ filters: [{ field: "a", op: "near" } as never] }), TypeError);
  assert.deepEqual(normalizeQuery({ filters: [{ field: "a", op: "range", min: 1, max: null }] }), { sort: [], filters: [{ field: "a", op: "range", min: 1 }] });
});

test("a range in flight across a query change never shows rows of the old query", async () => {
  const bridge = createMockBridge({ scheduling: "manual" });
  installMockBridge(bridge);
  const pending = connect(50, { latency: 40, bridge });
  await bridge.flush();
  const { view, client, collection } = await bridge.flushUntil(pending);
  const shown: string[] = [];
  collection.subscribe(() => {
    const { query, rows } = collection.current;
    // Every loaded row the page renders under the new query must be a row of that query.
    if (query.sort.length > 0) for (const row of rows) if (row.status === "loaded") shown.push(`${row.row!.id}`);
  });
  collection.setRange(0, 3);
  await bridge.flush();
  assert.equal(view.query("rows").loads.length, 1);
  // The query changes while the old query's rows are on their way.
  const sorted = collection.setQuery({ sort: [{ field: "id", descending: true }] });
  await bridge.flush();
  await sorted;
  await bridge.advance(40);
  await bridge.advance(40);
  await bridge.flush();
  assert.deepEqual(collection.current.rows.map(row => row.row?.id), [49, 48, 47]);
  assert.ok(shown.every(id => Number(id) >= 47), `old-query rows were shown: ${shown.join(",")}`);
  assert.deepEqual(view.query("rows").loads.map(load => load.version), [1, 2]);
  client.dispose();
});

test("rows of a version the state has not reached wait for it", async () => {
  const { bridge, view, client, collection } = await connect(10);
  // .NET loaded these rows after a change whose frame has not arrived yet.
  let ahead = true;
  bridge.intercept(({ kind }) => kind === "query-rows" && ahead
    ? { ok: true, version: 2, total: 10, start: 0, keys: ["9", "8"], rows: [{ id: 9, label: "row 9", size: 2 }, { id: 8, label: "row 8", size: 1 }] }
    : undefined);
  collection.setRange(0, 2);
  await bridge.flush();
  assert.ok(collection.current.rows.every(row => row.status === "loading"), "rows ahead of the state were shown");
  ahead = false;
  const loads = bridge.calls.filter(call => call.kind === "query-rows").length;
  view.update(state => ({ ...state, rows: { query: { sort: [{ field: "id", descending: true }], filters: [] }, total: 10, version: 2 } }));
  await bridge.flush();
  assert.deepEqual(collection.current.rows.map(row => row.row?.id), [9, 8]);
  assert.equal(bridge.calls.filter(call => call.kind === "query-rows").length, loads, "rows that were already current were loaded again");
  client.dispose();
});

test("setQuery sends one query at a time and the latest query only", async () => {
  const { bridge, client, collection } = await connect(100);
  const calls = ["1", "12", "123", "1234"].map(text => collection.setQuery({ filters: [{ field: "label", op: "contains", value: text }] }));
  await Promise.all(calls);
  const sent = bridge.calls.filter(call => call.kind === "query-set");
  assert.equal(sent.length, 2, "the queries typed while one was in flight were not coalesced");
  assert.deepEqual(client.snapshot.rows.query.filters, [{ field: "label", op: "contains", value: "1234" }]);
  client.dispose();
});

test("scrollToKey searches again when the query changed during the search, and requests answered busy are retried", async () => {
  const { bridge, client, collection } = await connect(1_000);
  const viewport = createQueryViewportController(collection, { estimateSize: 10, overscan: 0 });
  const unsubscribe = viewport.subscribe(() => {});
  const element = { scrollTop: 0, clientHeight: 100, addEventListener() {}, removeEventListener() {} } as unknown as HTMLElement;
  viewport.attach(element);
  let stale = true;
  bridge.intercept(({ kind }) => {
    if (kind !== "query-index" || !stale) return undefined;
    stale = false;
    return { ok: true, version: 0, index: 3 };
  });
  assert.equal(await viewport.scrollToKey("500"), true);
  assert.equal(bridge.calls.filter(call => call.kind === "query-index").length, 2);
  assert.equal(element.scrollTop, 5_000);
  let busy = 2;
  bridge.intercept(({ kind }) => kind === "query-rows" && busy-- > 0
    ? { ok: false, error: { kind: "rejected", reason: "busy", message: "busy" } } : undefined);
  element.scrollTop = 7_000;
  viewport.attach(null);
  viewport.attach(element);
  await until(() => viewport.current.items.length > 0 && viewport.current.items.every(item => item.status === "loaded"), bridge);
  assert.equal(viewport.current.items[0]!.row!.id, 700);
  assert.equal(collection.current.error, undefined);
  unsubscribe();
  viewport.dispose();
  client.dispose();
});

test("a long scroll keeps the client's rows bounded", async () => {
  const { bridge, client, collection } = await connect(20_000);
  for (let start = 0; start < 20_000; start += 400) {
    collection.setRange(start, 50);
    await until(() => collection.current.rows.every(row => row.status === "loaded"), bridge);
  }
  let loaded = 0;
  for (let index = 0; index < 20_000; index++) if (collection.row(index).status === "loaded") loaded++;
  assert.ok(loaded <= 2_000, `${loaded} rows stayed loaded`);
  assert.equal(collection.row(0).status, "loading");
  client.dispose();
});

test("setQuery rejects a query it cannot normalize instead of throwing", async () => {
  const { client, collection } = await connect(5);
  await assert.rejects(collection.setQuery({ filters: [{ field: "id", op: "near" } as never] }), TypeError);
  client.dispose();
});
