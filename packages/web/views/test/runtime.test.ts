import assert from "node:assert/strict";
import { cp, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { BridgeError, BridgeOperationUncertainError } from "../dist/index.js";
import { bridgeOperations, connectView, viewReferences } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge, type MockCall } from "../dist/mock.js";

type CounterState = { readonly count: number; readonly label: string };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Tests.CounterViewModel:fingerprint";
const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1000), label: bridgeWire.string };

function freshBridge(): MockBridge {
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  return installMockBridge(createMockBridge());
}

function connectCounter(route = "counter") {
  return connectView<CounterState>({ contract, route, members, operations: bridgeOperations });
}

const requests = (bridge: MockBridge, kind: string, route?: string): MockCall[] =>
  bridge.calls.filter(call => call.kind === kind && (route === undefined || call.route === route));
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

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
    commands: { increment: state => ({ count: (state["count"] as number) + 1 }) },
  });
  const client = await connectCounter();
  assert.deepEqual(client.snapshot, { count: 1, label: "one" });
  assert.equal("revision" in client.snapshot, false);

  const seen: CounterState[] = [];
  const stop = client.subscribe(state => seen.push(state));
  assert.deepEqual(seen, [{ count: 1, label: "one" }]);
  assert.deepEqual(await client.command("increment"), { count: 2, label: "one" });
  assert.deepEqual(await client.set("label", "two"), { count: 2, label: "two" });
  assert.equal(await client.can("increment"), true);
  counter.update({ count: 9 });
  assert.equal(client.snapshot.count, 9);
  assert.equal(seen.at(-1)?.count, 9);
  stop();
  counter.update({ count: 10 });
  assert.equal(seen.at(-1)?.count, 9);
  client.dispose();
});

test("an undecodable state push neither replaces the state nor advances the revision", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { reported, restore } = captureReports();
  try {
    counter.push({ kind: "state", revision: 50, state: { count: "not a number", label: "bad" } });
    assert.equal(reported.length, 1);
    assert.deepEqual(client.snapshot, { count: 1, label: "one" });
    counter.push({ kind: "state", revision: 49, state: { count: 2, label: "accepted" } });
    assert.deepEqual(client.snapshot, { count: 2, label: "accepted" });
    counter.push({ kind: "state", revision: 10, state: { count: 3, label: "stale" } });
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

test("a disposed client closes its presentation, keeps its last snapshot and ignores late subscribers", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 5, label: "five" } });
  const client = await connectCounter();
  const presentation = requests(bridge, "open")[0]?.message["presentation"];
  assert.equal(typeof presentation, "string");
  client.dispose();
  client.dispose();
  await settle();
  assert.deepEqual(requests(bridge, "close").map(call => call.message["presentation"]), [presentation]);
  assert.deepEqual(client.snapshot, { count: 5, label: "five" });
  const late: CounterState[] = [];
  const stop = client.subscribe(state => late.push(state));
  assert.deepEqual(late, [{ count: 5, label: "five" }]);
  stop();
  counter.update({ count: 6 });
  assert.deepEqual(late, [{ count: 5, label: "five" }]);
  await assert.rejects(client.set("count", 1), (error: unknown) =>
    error instanceof BridgeError && error.kind === "released" && error.route === "counter");
  assert.equal((host[runtimeKey] as { routes: Map<string, unknown> }).routes.has("counter"), false);
});

test("presentations of one route share its state until the last one is disposed", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
  const first = await connectCounter();
  const second = await connectCounter();
  const opened = requests(bridge, "open").map(call => call.message["presentation"]);
  assert.equal(opened.length, 2, "each presentation sends its own open");
  assert.notEqual(opened[0], opened[1]);
  assert.equal(first.snapshot, second.snapshot);
  first.dispose();
  counter.update({ count: 2 });
  assert.equal(second.snapshot.count, 2, "the route stays open for the remaining presentation");
  assert.equal(first.snapshot.count, 1);
  second.dispose();
  const routes = (host[runtimeKey] as { routes: Map<string, unknown> }).routes;
  assert.equal(routes.has("counter"), false);
  const third = await connectCounter();
  await assert.rejects(connectView({ contract: "Other:fingerprint", route: "counter", members }),
    (error: unknown) => error instanceof BridgeError && /incompatible/.test(error.message));
  third.dispose();
});

// Specs/application, "Presentations": after a new connection the client sends hello with a new session and opens
// each route again with the same presentation ids, retrying while the producer answers `ignored`.
test("a reconnect sends a new hello and opens each presentation again with its id", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("contentabc", { state: { count: 1, label: "before" } });
  const client = await connectCounter("contentabc");
  const presentation = requests(bridge, "open")[0]?.message["presentation"];
  bridge.disconnect();
  counter.update({ label: "after" });
  assert.equal(client.snapshot.label, "before", "a push during the outage is not delivered");
  let ignored = 1;
  const stop = bridge.intercept(call => {
    if (call.kind !== "open" || ignored === 0) return undefined;
    ignored--;
    return { ok: true, revision: counter.revision, state: counter.state, presentation: "ignored" };
  });
  try {
    bridge.reconnect();
    const deadline = Date.now() + 5_000;
    while (requests(bridge, "open").length < 3 && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
    await settle();
  } finally { stop(); }
  const opens = requests(bridge, "open").map(call => call.message["presentation"]);
  assert.deepEqual(opens, [presentation, presentation, presentation], "an ignored open is retried with the same id");
  const sessions = requests(bridge, "hello").map(call => call.message["session"]);
  assert.equal(sessions.length, 2);
  assert.notEqual(sessions[0], sessions[1], "the new connection has a new session");
  assert.equal(client.snapshot.label, "after", "the reopened route takes the state of the new connection");
  client.dispose();
  await settle();
  assert.deepEqual(requests(bridge, "close").map(call => call.message["presentation"]), [presentation]);
});

test("calls fail with BridgeError, and with disconnected when the transport is unavailable", async () => {
  const bridge = freshBridge();
  bridge.view("counter", {
    state: { count: 1, label: "one" },
    commands: { fail: () => { throw Object.assign(new Error("No."), { kind: "rejected" }); }, increment: () => undefined },
  });
  const client = await connectCounter();
  await assert.rejects(client.command("fail"), (error: unknown) => error instanceof BridgeError && error.kind === "rejected" && error.route === "counter");
  await assert.rejects(client.command("missing"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.reason === "invalid-request");
  bridge.disconnect();
  await assert.rejects(client.command("increment"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
  client.dispose();
});

test("operations start, retry a failed watch and recover by operation id", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" }, operations: { save: "manual" } });
  let dropWatch = true;
  const stop = bridge.intercept(call => {
    if (call.kind !== "watch" || !dropWatch) return undefined;
    dropWatch = false;
    throw new Error("dropped");
  });
  const client = await connectCounter();
  try {
    const operation = await client.start("save", "request-1", 3, { decode: value => bridgeWire.bigint(value) });
    assert.equal(operation.requestId, "request-1");
    assert.deepEqual(requests(bridge, "start")[0]?.message, { kind: "start", route: "counter", member: "save", operation: "request-1",
      contract, input: 3 });
    await assert.rejects(operation.wait(), (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-1");
    // The next read watches again.
    const completion = operation.completion;
    await settle();
    counter.operations("save")[0]!.succeed("7");
    const status = await completion;
    assert.equal(status.kind, "succeeded");
    assert.equal(status.kind === "succeeded" ? status.result : undefined, 7n);
    assert.equal(requests(bridge, "watch").length, 2);
    await assert.rejects(client.recover("save", "request-2", { decode: value => value }),
      (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-2");
    await assert.rejects(client.start("save", "", undefined, { decode: value => value }), RangeError);
  } finally {
    stop();
    client.dispose();
  }
});

test("operations reject without injected operation support and send no request", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectView<CounterState>({ contract, route: "counter", members });
  const callsBefore = bridge.calls.length;
  const unsupported = (error: unknown) => error instanceof BridgeError && error.kind === "failed" && /operation support/.test(error.message);
  await assert.rejects(client.start("save", "request-1", undefined, { decode: value => value }), unsupported);
  await assert.rejects(client.recover("save", "request-1", { decode: value => value }), unsupported);
  assert.equal(bridge.calls.length, callsBefore);
  client.dispose();
});

test("content references keep their identity per id", () => {
  const references = viewReferences(id => ({ kind: "counter", connect: async () => id }));
  assert.equal(references("a"), references("a"));
  assert.notEqual(references("a"), references("b"));
});

test("a runtime of another protocol or Bridge at the shared key is replaced", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  // An HMR-retained runtime of a protocol-3 generated client.
  const legacy = { bridge, generation: Symbol(), mountSession: "legacy", routes: new Map() };
  host[runtimeKey] = legacy;
  const client = await connectCounter();
  const runtime = host[runtimeKey] as { protocol?: number; operations?: Map<string, unknown>; routes: Map<string, unknown> };
  assert.notEqual(runtime, legacy);
  assert.equal(runtime.protocol, 4);
  assert.ok(runtime.operations instanceof Map);
  assert.ok(runtime.routes.has("counter"));
  assert.equal(legacy.routes.size, 0);

  // A new Bridge gets its own runtime; the old one stops listening.
  const next = installMockBridge(createMockBridge());
  next.view("counter", { state: { count: 7, label: "next" } });
  const other = await connectCounter();
  assert.notEqual(host[runtimeKey], runtime);
  assert.equal(other.snapshot.count, 7);
  other.dispose();
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
    assert.ok(new a.BridgeOperationUncertainError("copy", { contract: "contract", requestId: "id" }) instanceof b.BridgeOperationUncertainError);
    class CustomError extends a.BridgeError {}
    assert.ok(!(new b.BridgeError("failed", "copy") instanceof CustomError));
    assert.ok(new CustomError("failed", "custom") instanceof CustomError);

    const bridge = freshBridge();
    const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
    const options = { contract, route: "counter", members };
    const first = await aGenerated.connectView<CounterState>(options);
    const second = await bGenerated.connectView<CounterState>(options);
    assert.equal(requests(bridge, "hello").length, 1, "both copies use the connection of one runtime");
    counter.update({ count: 2 });
    assert.equal(first.snapshot.count, 2);
    assert.equal(second.snapshot, first.snapshot);
    first.dispose();
    second.dispose();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
