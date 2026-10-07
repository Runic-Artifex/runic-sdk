import assert from "node:assert/strict";
import { cp, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { BridgeError, BridgeOperationUncertainError } from "../dist/index.js";
import { bridgeOperations, connectView, viewReferences } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

type CounterState = { readonly count: number; readonly label: string };
type Wire = { readonly revision: number; readonly count: unknown; readonly label: unknown };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;

function hydrate(wire: Wire): CounterState {
  return { count: bridgeWire.integer(wire.count, 0, 1000), label: bridgeWire.string(wire.label) };
}

function freshBridge(): MockBridge {
  delete host[runtimeKey];
  return installMockBridge(createMockBridge());
}

function connectCounter(route = "counter", mount = false) {
  return connectView({ contract: "Tests.CounterViewModel:fingerprint", route, mount, hydrate, operations: bridgeOperations });
}

const routeCalls = (bridge: MockBridge, name: string) => bridge.calls.filter(call => call.name === name);

function captureReports() {
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  const reported: unknown[] = [];
  target.reportError = error => { reported.push(error); };
  return { reported, restore: () => { target.reportError = previous; } };
}

test("a connected client reads, updates and pushes state through the mock Bridge", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", {
    state: { count: 1, label: "one" },
    routes: { Increment: state => ({ count: (state["count"] as number) + 1 }) },
  });
  const client = await connectCounter();
  assert.deepEqual(client.snapshot, { count: 1, label: "one" });
  assert.equal("revision" in client.snapshot, false);

  const seen: CounterState[] = [];
  const stop = client.subscribe(state => seen.push(state));
  assert.deepEqual(seen, [{ count: 1, label: "one" }]);
  assert.deepEqual(await client.command("counterIncrement"), { count: 2, label: "one" });
  assert.deepEqual(await client.invoke("counterSetLabel", "two"), { count: 2, label: "two" });
  assert.equal(await client.query("counterCanIncrement"), true);
  counter.update({ count: 9 });
  assert.equal(client.snapshot.count, 9);
  assert.equal(seen.at(-1)?.count, 9);
  stop();
  counter.update({ count: 10 });
  assert.equal(seen.at(-1)?.count, 9);
  client.dispose();
});

test("an undecodable push neither replaces the state nor advances the revision", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { reported, restore } = captureReports();
  try {
    const push = host["__counterChanged"] as (state: unknown) => unknown;
    push({ revision: 50, count: "not a number", label: "bad" });
    assert.equal(reported.length, 1);
    assert.deepEqual(client.snapshot, { count: 1, label: "one" });
    push({ revision: 49, count: 2, label: "accepted" });
    assert.deepEqual(client.snapshot, { count: 2, label: "accepted" });
    push({ revision: 10, count: 3, label: "stale" });
    assert.deepEqual(client.snapshot, { count: 2, label: "accepted" });
  } finally {
    restore();
    client.dispose();
  }
});

test("a throwing subscriber does not stop delivery to later subscribers", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { reported, restore } = captureReports();
  try {
    let initial = true;
    client.subscribe(() => { if (!initial) throw new Error("listener failure"); initial = false; });
    const counts: number[] = [];
    client.subscribe(state => counts.push(state.count));
    counter.update({ count: 4 });
    assert.deepEqual(counts, [1, 4]);
    assert.equal(reported.length, 1);
  } finally {
    restore();
    client.dispose();
  }
});

test("a disposed client keeps its last snapshot and ignores late subscribers", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 5, label: "five" } });
  const client = await connectCounter();
  client.dispose();
  client.dispose();
  assert.deepEqual(client.snapshot, { count: 5, label: "five" });
  const late: CounterState[] = [];
  const stop = client.subscribe(state => late.push(state));
  assert.deepEqual(late, [{ count: 5, label: "five" }]);
  stop();
  counter.update({ count: 6 });
  assert.deepEqual(late, [{ count: 5, label: "five" }]);
  await assert.rejects(client.invoke("counterSetCount", 1), (error: unknown) =>
    error instanceof BridgeError && error.kind === "disconnected");
  assert.equal(host["__counterChanged"], undefined);
});

test("leases of one route share a snapshot until the last one is disposed", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  const first = await connectCounter();
  const second = await connectCounter();
  assert.equal(routeCalls(bridge, "counterSnapshot").length, 1);
  assert.equal(first.snapshot, second.snapshot);
  first.dispose();
  assert.equal(typeof host["__counterChanged"], "function");
  second.dispose();
  assert.equal(host["__counterChanged"], undefined);
  const third = await connectCounter();
  await assert.rejects(connectView({ contract: "Other:fingerprint", route: "counter", mount: false, hydrate }),
    (error: unknown) => error instanceof BridgeError && /incompatible/.test(error.message));
  third.dispose();
});

test("a reconnect re-reads live routes and re-sends mount tokens until .NET accepts them", async () => {
  const bridge = freshBridge();
  const mounts = ["ok", "ignored", "ok"];
  const counter = bridge.view("contentabc", {
    state: { count: 1, label: "before" },
    routes: { Mount: () => mounts.shift() ?? "unexpected" },
  });
  const client = await connectCounter("contentabc", true);
  const token = routeCalls(bridge, "contentabcMount")[0]?.args[0];
  assert.equal(typeof token, "string");
  bridge.disconnect();
  counter.update({ label: "after" });
  assert.equal(client.snapshot.label, "after", "a push during the outage is still accepted");
  bridge.reconnect();
  const deadline = Date.now() + 5_000;
  while (routeCalls(bridge, "contentabcMount").length < 3 && Date.now() < deadline)
    await new Promise(resolve => setTimeout(resolve, 20));
  const remounts = routeCalls(bridge, "contentabcMount");
  assert.equal(remounts.length, 3);
  assert.ok(remounts.every(call => call.args[0] === token));
  assert.ok(routeCalls(bridge, "contentabcSnapshot").length >= 2);
  client.dispose();
  assert.equal(routeCalls(bridge, "contentabcUnmount")[0]?.args[0], token);
});

test("calls fail with BridgeError when the transport is unavailable", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" }, routes: { Fail: () => { throw Object.assign(new Error("No."), { kind: "rejected" }); } } });
  const client = await connectCounter();
  await assert.rejects(client.command("counterFail"), (error: unknown) => error instanceof BridgeError && error.kind === "rejected");
  await assert.rejects(client.command("counterMissing"), (error: unknown) => error instanceof BridgeError && error.kind === "failed");
  bridge.disconnect();
  await assert.rejects(client.command("counterIncrement"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
  client.dispose();
});

test("operations admit, retry a dropped wait and recover by request id", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  const contract = "Tests.CounterViewModel:fingerprint:counter";
  bridge.route("counterStartSave", payload => JSON.stringify({ contract, requestId: JSON.parse(String(payload)).requestId, kind: "accepted", terminal: null }));
  let dropWait = true;
  bridge.route("__runicOperationWait", identity => {
    if (dropWait) { dropWait = false; throw new Error("dropped"); }
    return JSON.stringify({ contract, requestId: JSON.parse(String(identity)).requestId, kind: "succeeded", result: "7" });
  });
  bridge.route("__runicOperationStatus", identity => JSON.stringify({ contract, requestId: JSON.parse(String(identity)).requestId, kind: "unknown" }));
  const client = await connectCounter();
  const operation = await client.startOperation("Save", "request-1", () => JSON.stringify({ requestId: "request-1", input: 3 }),
    value => bridgeWire.bigint(value));
  await assert.rejects(operation.wait(), (error: unknown) => error instanceof BridgeOperationUncertainError);
  const completion = await operation.completion;
  assert.equal(completion.kind, "succeeded");
  assert.equal(completion.result, 7n);
  await assert.rejects(client.recoverOperation("Save", "request-2", value => value),
    (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-2");
  await assert.rejects(client.startOperation("Save", "", () => "", value => value), RangeError);
  client.dispose();
});

test("content references keep their identity per id", () => {
  const references = viewReferences(id => ({ kind: "counter", connect: async () => id }));
  assert.equal(references("a"), references("a"));
  assert.notEqual(references("a"), references("b"));
});

test("an HMR-retained runtime from an earlier generated client gains new fields", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  host[runtimeKey] = { bridge, generation: Symbol(), mountSession: "legacy", routes: new Map() };
  const client = await connectCounter();
  const runtime = host[runtimeKey] as { operations?: Map<string, unknown>; routes: Map<string, unknown> };
  assert.ok(runtime.operations instanceof Map);
  assert.ok(runtime.routes.has("counter"));
  client.dispose();
});

test("copies of the package share one page runtime and one BridgeError identity", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-views-copies-"));
  try {
    const dist = fileURLToPath(new URL("../dist/", import.meta.url));
    await cp(dist, join(directory, "a"), { recursive: true });
    await cp(dist, join(directory, "b"), { recursive: true });
    const a = await import(pathToFileURL(join(directory, "a/index.js")).href) as typeof import("../dist/index.js");
    const b = await import(pathToFileURL(join(directory, "b/index.js")).href) as typeof import("../dist/index.js");
    const aGenerated = await import(pathToFileURL(join(directory, "a/generated.js")).href) as typeof import("../dist/generated.js");
    const bGenerated = await import(pathToFileURL(join(directory, "b/generated.js")).href) as typeof import("../dist/generated.js");
    assert.notEqual(aGenerated.connectView, bGenerated.connectView);
    assert.ok(new a.BridgeError("failed", "copy") instanceof b.BridgeError);
    assert.ok(new b.BridgeError("failed", "copy") instanceof BridgeError);
    assert.ok(!(new Error("plain") instanceof a.BridgeError));
    assert.ok(new a.BridgeOperationUncertainError("contract", "id", "copy") instanceof b.BridgeOperationUncertainError);
    class CustomError extends a.BridgeError {}
    assert.ok(!(new b.BridgeError("failed", "copy") instanceof CustomError));
    assert.ok(new CustomError("failed", "custom") instanceof CustomError);

    const bridge = freshBridge();
    const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
    const options = { contract: "Tests.CounterViewModel:fingerprint", route: "counter", mount: false, hydrate };
    const first = await aGenerated.connectView(options);
    const second = await bGenerated.connectView(options);
    assert.equal(routeCalls(bridge, "counterSnapshot").length, 1);
    counter.update({ count: 2 });
    assert.equal(first.snapshot.count, 2);
    assert.equal(second.snapshot, first.snapshot);
    first.dispose();
    second.dispose();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
