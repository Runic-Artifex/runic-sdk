import assert from "node:assert/strict";
import { cp, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { bridgeOperations, connectView, defineCollection, defineCollections } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { BridgeError, BridgeOperationUncertainError, onBridgeDiagnostic, waitForBridge,
  type BridgeDiagnostic } from "../dist/index.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

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

function observe() {
  const diagnostics: BridgeDiagnostic[] = [];
  const reported: unknown[] = [];
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  target.reportError = error => { reported.push(error); };
  const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
  return { diagnostics, reported, restore: () => { stop(); target.reportError = previous; } };
}

const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const failureOf = (promise: Promise<unknown>) => promise.then(() => undefined, (error: unknown) => error);

test("waitForBridge tells a missing Bridge from one that does not connect", async () => {
  const { diagnostics, restore } = observe();
  try {
    delete host["__runicBridge"];
    await assert.rejects(waitForBridge({ timeout: 0 }), (error: unknown) =>
      error instanceof BridgeError && error.kind === "unavailable"
      && /window\.__runicBridge/.test(error.message) && /dotnet runic dev/.test(error.message) && /installMockBridge/.test(error.message));
    assert.equal(diagnostics.at(-1)?.code, "unavailable");
    const reportedBefore = diagnostics.length;
    await assert.rejects(waitForBridge({ timeout: 0 }), (error: unknown) => error instanceof BridgeError && error.kind === "unavailable");
    assert.equal(diagnostics.length, reportedBefore, "A missing Bridge must reach DevTools once per page.");

    const bridge = freshBridge();
    bridge.disconnect();
    await assert.rejects(waitForBridge({ timeout: 60 }), (error: unknown) =>
      error instanceof BridgeError && error.kind === "timeout" && /within 60 ms/.test(error.message));
    await assert.rejects(waitForBridge({ timeout: -1 }), RangeError);
    bridge.reconnect();
    assert.equal(await waitForBridge({ timeout: 0 }), bridge);
    assert.equal(await waitForBridge(), bridge);
  } finally {
    restore();
  }
});

test("a connect timeout bounds the wait for the host Bridge", async () => {
  const { restore } = observe();
  try {
    const bridge = freshBridge();
    bridge.view("counter", { state: { count: 1, label: "one" } });
    bridge.disconnect();
    const started = Date.now();
    await assert.rejects(connectView({ contract, route: "counter", members, timeout: 60 }),
      (error: unknown) => error instanceof BridgeError && error.kind === "timeout" && /within 60 ms/.test(error.message));
    assert.ok(Date.now() - started < 4_000, "The connect timeout did not replace the 5000 ms default.");
    // A later connection within the timeout succeeds.
    setTimeout(() => bridge.reconnect(), 20);
    const client = await connectView({ contract, route: "counter", members, timeout: 2_000 });
    client.dispose();
  } finally {
    restore();
  }
});

test("a failed request keeps its route, producer detail and transport cause, and is reported once", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" },
    commands: { throws: () => { throw new TypeError("mock handler"); } } });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  try {
    const detail = { type: "System.InvalidOperationException", message: "Disk full.", stack: "System.InvalidOperationException: Disk full.\n   at Save()" };
    counter.failNext({ kind: "command", member: "save" }, { kind: "failed", message: "Save failed.", detail });
    const failure = await failureOf(client.command("save"));
    assert.ok(failure instanceof BridgeError);
    assert.equal(failure.kind, "failed");
    assert.equal(failure.message, "Save failed.");
    assert.equal(failure.route, "counter");
    assert.deepEqual(failure.detail, detail);
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.kind, diagnostic.code, diagnostic.route]), [["error", "failed", "counter"]]);
    assert.deepEqual(diagnostics[0]?.detail, detail);

    const stop = bridge.intercept(call => call.member === "bounded"
      ? { ok: false, error: { kind: "failed", message: "Bounded failed.", detail: "not an object" } } : undefined);
    const bounded = await failureOf(client.command("bounded"));
    stop();
    assert.ok(bounded instanceof BridgeError && bounded.message === "Bounded failed." && bounded.detail === undefined,
      "A malformed detail must be dropped.");

    const transport = new Error("socket closed");
    const stopTransport = bridge.intercept(call => { if (call.member === "broken") throw transport; return undefined; });
    const broken = await failureOf(client.command("broken"));
    stopTransport();
    assert.ok(broken instanceof BridgeError && broken.kind === "failed");
    assert.equal(broken.cause, transport);
    assert.equal(broken.route, "counter");

    const mocked = await failureOf(client.command("throws"));
    assert.ok(mocked instanceof BridgeError && mocked.detail?.type === "TypeError" && mocked.detail.stack?.includes("mock handler"));
  } finally {
    restore();
    client.dispose();
  }
});

const decodeRow = (wire: unknown) => ({ id: bridgeWire.integer(bridgeWire.object(wire, value => value)["id"], 0, 100) });
const rowsOptions = (route: string) => ({
  contract: `${route}:errors`, route, members: { rows: (value: unknown) => bridgeWire.array(value, decodeRow) },
  collections: defineCollections({ rows: defineCollection(decodeRow, row => String(row.id)) }),
});

test("an unusable collection change is reported before the client recovers", async () => {
  const bridge = freshBridge();
  const rows = bridge.view("rows", { state: { rows: [{ id: 1 }] } });
  const client = await connectView(rowsOptions("rows"));
  const { reported, restore } = observe();
  let recoveries = 0;
  const stop = bridge.intercept(call => {
    if (call.kind !== "snapshot") return undefined;
    recoveries++;
    return { ok: true, revision: 50, state: { rows: [{ id: 5 }] } };
  });
  try {
    rows.push({ kind: "frame", baseRevision: rows.revision, revision: rows.revision + 1,
      changes: [{ field: "rows", kind: "remove", index: 0, oldIndex: 0, keys: ["missing"], items: [] }] });
    await settle();
    assert.equal(reported.length, 1);
    const error = reported[0];
    assert.ok(error instanceof BridgeError && error.kind === "failed" && error.route === "rows" && error.cause instanceof Error);
    assert.equal(recoveries, 1);
    assert.deepEqual(client.snapshot.rows, [{ id: 5 }]);
  } finally {
    stop();
    restore();
    client.dispose();
  }
});

// state-changes.md, "Recovery": four reads in total, 250, 500 and 1,000 ms apart. Each failure that is retried is
// a diagnostic; the last one is also reported as an error, and the client keeps its last state.
test("a recovery read that keeps failing is retried with backoff and reported once as an error", async () => {
  const bridge = freshBridge();
  const rows = bridge.view("rowsp", { state: { rows: [{ id: 1 }] } });
  const client = await connectView(rowsOptions("rowsp"));
  const { diagnostics, reported, restore } = observe();
  let recoveries = 0;
  const stop = bridge.intercept(call => {
    if (call.kind !== "snapshot") return undefined;
    recoveries++;
    return { ok: false, error: { kind: "failed", message: `Snapshot ${recoveries} failed.` } };
  });
  try {
    // A frame that does not continue the current state starts a recovery.
    rows.push({ kind: "frame", baseRevision: rows.revision + 10, revision: rows.revision + 11,
      changes: [{ field: "rows", kind: "set", value: [{ id: 9 }] }] });
    for (let waited = 0; recoveries < 4 && waited < 5000; waited += 50) await new Promise(resolve => setTimeout(resolve, 50));
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(recoveries, 4);
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.code, diagnostic.route, diagnostic.message]),
      [1, 2, 3, 4].map(attempt => ["failed", "rowsp", `Snapshot ${attempt} failed.`]));
    assert.equal(reported.length, 1);
    assert.ok(reported[0] instanceof BridgeError && reported[0].message === "Snapshot 4 failed.");
    assert.deepEqual(client.snapshot.rows, [{ id: 1 }]);
  } finally {
    stop();
    restore();
    client.dispose();
  }
});

test("a failed open after a reconnect is reported instead of dropped", async () => {
  const bridge = freshBridge();
  bridge.view("contenterr", { state: { count: 1, label: "one" } });
  const client = await connectCounter("contenterr");
  const { reported, restore } = observe();
  const transport = new Error("open refused");
  const stop = bridge.intercept(call => { if (call.kind === "open") throw transport; return undefined; });
  try {
    bridge.disconnect();
    bridge.reconnect();
    const deadline = Date.now() + 2_000;
    while (!reported.some(error => error instanceof BridgeError && error.route === "contenterr") && Date.now() < deadline) await settle();
    const reopen = reported.find(error => error instanceof BridgeError && error.route === "contenterr");
    assert.ok(reopen instanceof BridgeError && reopen.kind === "failed" && reopen.cause === transport);
    assert.deepEqual(client.snapshot, { count: 1, label: "one" }, "the client keeps its last state");
  } finally {
    stop();
    restore();
    client.dispose();
  }
});

test("an error is reported once across copies of the package", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-views-once-"));
  const { diagnostics, restore } = observe();
  try {
    const dist = fileURLToPath(new URL("../dist/", import.meta.url));
    await cp(dist, join(directory, "a"), { recursive: true });
    await cp(dist, join(directory, "b"), { recursive: true });
    type Diagnostics = typeof import("../dist/diagnostics.js");
    const a = await import(pathToFileURL(join(directory, "a/diagnostics.js")).href) as Diagnostics;
    const b = await import(pathToFileURL(join(directory, "b/diagnostics.js")).href) as Diagnostics;
    const error = new BridgeError("failed", "once", { route: "counter" });
    a.emitErrorDiagnostic(error);
    b.emitErrorDiagnostic(error);
    assert.equal(diagnostics.filter(diagnostic => diagnostic.error === error).length, 1);
    const frozen = Object.freeze(new Error("frozen"));
    a.emitErrorDiagnostic(frozen);
    a.emitErrorDiagnostic(frozen);
    assert.equal(diagnostics.filter(diagnostic => diagnostic.error === frozen).length, 1);
  } finally {
    restore();
    await rm(directory, { recursive: true, force: true });
  }
});

test("an invalid state push is reported with its route and decoder message", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  try {
    counter.push({ kind: "state", revision: 99, state: { count: "bad", label: "bad" } });
    const invalid = diagnostics.at(-1);
    assert.equal(invalid?.route, "counter");
    assert.ok(invalid?.error instanceof BridgeError && invalid.error.kind === "failed" && invalid.error.cause instanceof Error);
    assert.match(invalid.message, /count/);
    assert.match(invalid.message, /bounded integer/);
    counter.update({ count: 2 });
    assert.equal(client.snapshot.count, 2, "a later frame still applies");
  } finally {
    restore();
    client.dispose();
  }
});

test("an operation wait with a timeout cancels and resolves to timed-out", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" }, operations: { save: "manual" } });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  const cancels: string[] = [];
  let onCancel: (operation: string) => void = () => {};
  let statusFails = false;
  const stop = bridge.intercept(call => {
    if (call.kind === "cancel") { cancels.push(call.message["operation"] as string); onCancel(call.message["operation"] as string); }
    if (call.kind === "status" && statusFails) throw new Error("status lost");
    return undefined;
  });
  const running = (operation: string) => counter.operations("save").find(item => item.requestId === operation)!;
  const codec = { decode: (value: unknown) => value };
  try {
    const operation = await client.start("save", "slow", null, codec);
    const status = await operation.wait({ timeout: 20 });
    assert.deepEqual(status, { contract, requestId: "slow", kind: "timed-out", cancellation: "cancellation-requested" });
    assert.deepEqual(cancels, ["slow"]);
    const timedOut = diagnostics.at(-1);
    assert.equal(timedOut?.kind, "operation");
    assert.equal(timedOut?.code, "timed-out");
    assert.equal(timedOut?.route, "counter");
    assert.equal(timedOut?.member, "save");
    assert.equal(timedOut?.requestId, "slow");
    await assert.rejects(operation.wait({ timeout: Number.NaN }), RangeError);

    // The operation finished before the cancel arrived: it keeps its real terminal status.
    onCancel = id => running(id).fail("The operation failed.", { type: "System.IO.IOException", message: "Locked." });
    const raced = await client.start("save", "raced", null, codec);
    const terminal = await raced.wait({ timeout: 0 });
    assert.equal(terminal.kind, "failed", "An operation that finished before the cancel keeps its real terminal state.");
    assert.deepEqual(terminal.kind === "failed" ? terminal.error.detail : undefined, { type: "System.IO.IOException", message: "Locked." });

    // The wait settles while the cancel reports not-running; a failing status read must not hide it.
    onCancel = id => running(id).succeed();
    statusFails = true;
    const settled = await client.start("save", "settled", null, codec);
    assert.equal((await settled.wait({ timeout: 0 })).kind, "succeeded");
    statusFails = false;

    onCancel = () => { throw new Error("cancel lost"); };
    const unobserved = await client.start("save", "lost", null, codec);
    const lost = await unobserved.wait({ timeout: 0 });
    assert.equal(lost.kind === "timed-out" ? lost.cancellation : lost.kind, "unobserved");
    assert.ok(diagnostics.some(diagnostic => diagnostic.code === "uncertain" && diagnostic.error instanceof BridgeOperationUncertainError
      && ((diagnostic.error.cause as Error).cause as Error).message === "cancel lost"));
  } finally {
    stop();
    restore();
    client.dispose();
  }
});
