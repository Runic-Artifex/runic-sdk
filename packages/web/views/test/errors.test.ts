import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, BridgeOperationUncertainError, bridgeWire, connectView, defineCollection, onBridgeDiagnostic, waitForBridge,
  type BridgeDiagnostic } from "../dist/index.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

type CounterState = { readonly count: number; readonly label: string };
type Wire = { readonly revision: number; readonly count: unknown; readonly label: unknown };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Tests.CounterViewModel:fingerprint:counter";

function hydrate(wire: Wire): CounterState {
  return { count: bridgeWire.integer(wire.count, 0, 1000), label: bridgeWire.string(wire.label) };
}

function freshBridge(): MockBridge {
  delete host[runtimeKey];
  return installMockBridge(createMockBridge());
}

function connectCounter(route = "counter", mount = false) {
  return connectView({ contract: "Tests.CounterViewModel:fingerprint", route, mount, hydrate });
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

test("waitForBridge tells a missing Bridge from one that does not connect", async () => {
  const { diagnostics, restore } = observe();
  try {
    delete host["__runicBridge"];
    await assert.rejects(waitForBridge({ timeout: 0 }), (error: unknown) =>
      error instanceof BridgeError && error.kind === "unavailable"
      && /window\.__runicBridge/.test(error.message) && /dotnet runic dev/.test(error.message) && /installMockBridge/.test(error.message));
    assert.equal(diagnostics.at(-1)?.code, "unavailable");

    const bridge = freshBridge();
    bridge.disconnect();
    await assert.rejects(waitForBridge(60), (error: unknown) =>
      error instanceof BridgeError && error.kind === "timeout" && /within 60 ms/.test(error.message));
    await assert.rejects(waitForBridge({ timeout: -1 }), RangeError);
    bridge.reconnect();
    assert.equal(await waitForBridge({ timeout: 0 }), bridge);
    assert.equal(await waitForBridge(), bridge);
  } finally {
    restore();
  }
});

test("a failed route keeps its route, .NET detail and transport cause, and is reported once", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  try {
    const detail = { type: "System.InvalidOperationException", message: "Disk full.", stack: "System.InvalidOperationException: Disk full.\n   at Save()" };
    bridge.route("counterSave", () => JSON.stringify({ ok: false, state: null, error: { kind: "failed", message: "Save failed.", detail } }));
    const failure = await client.command("counterSave").then(() => undefined, (error: unknown) => error);
    assert.ok(failure instanceof BridgeError);
    assert.equal(failure.kind, "failed");
    assert.equal(failure.message, "Save failed.");
    assert.equal(failure.route, "counterSave");
    assert.deepEqual(failure.detail, detail);
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.kind, diagnostic.code, diagnostic.route]), [["error", "failed", "counterSave"]]);
    assert.deepEqual(diagnostics[0]?.detail, detail);

    bridge.route("counterBounded", () => JSON.stringify({ ok: false, state: null, error: { kind: "failed", message: "Bounded failed.", detail: "not an object" } }));
    const bounded = await client.command("counterBounded").then(() => undefined, (error: unknown) => error);
    assert.ok(bounded instanceof BridgeError && bounded.detail === undefined, "A malformed detail must be dropped.");

    const transport = new Error("socket closed");
    bridge.route("counterBroken", () => { throw transport; });
    const broken = await client.command("counterBroken").then(() => undefined, (error: unknown) => error);
    assert.ok(broken instanceof BridgeError);
    assert.equal(broken.cause, transport);
    assert.equal(broken.route, "counterBroken");

    bridge.view("counter", { state: { count: 1, label: "one" }, routes: { Throw: () => { throw new TypeError("mock handler"); } } });
    const mocked = await client.command("counterThrow").then(() => undefined, (error: unknown) => error);
    assert.ok(mocked instanceof BridgeError && mocked.detail?.type === "TypeError" && mocked.detail.stack?.includes("mock handler"));
  } finally {
    restore();
    client.dispose();
  }
});

test("an unusable collection change is reported before the client recovers", async () => {
  delete host[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("rows", { state: { rows: [{ id: 1 }] } });
  const decodeRow = (wire: unknown) => ({ id: bridgeWire.integer(bridgeWire.object(wire, value => value)["id"], 0, 100) });
  const client = await connectView({ contract: "rows:errors", route: "rows", mount: false,
    collections: { rows: defineCollection(decodeRow, row => String(row.id)) },
    hydrate: (wire: { rows: unknown[] }) => ({ rows: wire.rows.map(decodeRow) }) });
  const { reported, restore } = observe();
  try {
    let recoveries = 0;
    bridge.route("rowsSnapshot", () => { recoveries++; return JSON.stringify({ ok: true, state: { revision: 5, rows: [{ id: 5 }] }, error: null }); });
    const push = host["__rowsChanged"] as (wire: unknown) => void;
    push({ __runicDelta: 1, baseRevision: 1, revision: 2,
      changes: [{ field: "rows", kind: "remove", index: 0, oldIndex: 0, keys: ["missing"], items: [] }] });
    await settle();
    assert.equal(reported.length, 1);
    const error = reported[0];
    assert.ok(error instanceof BridgeError && error.route === "__rowsChanged" && error.cause instanceof Error);
    assert.equal(recoveries, 1);
    assert.deepEqual(client.snapshot.rows, [{ id: 5 }]);
  } finally {
    restore();
    client.dispose();
  }
});

test("a failed remount after a reconnect is reported instead of dropped", async () => {
  const bridge = freshBridge();
  bridge.view("contenterr", { state: { count: 1, label: "one" } });
  const client = await connectCounter("contenterr", true);
  const { reported, restore } = observe();
  try {
    bridge.route("contenterrMount", () => { throw new Error("mount refused"); });
    bridge.disconnect();
    bridge.reconnect();
    const deadline = Date.now() + 2_000;
    while (!reported.some(error => error instanceof BridgeError && error.route === "contenterrMount") && Date.now() < deadline) await settle();
    const remount = reported.find(error => error instanceof BridgeError && error.route === "contenterrMount");
    assert.ok(remount instanceof BridgeError && remount.cause instanceof Error);
  } finally {
    restore();
    client.dispose();
  }
});

test("a listener failure is reported with its route", async () => {
  const bridge = freshBridge();
  const counter = bridge.view("counter", { state: { count: 1, label: "one" } });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  try {
    const push = host["__counterChanged"] as (state: unknown) => unknown;
    push({ revision: 99, count: "bad", label: "bad" });
    const invalid = diagnostics.at(-1);
    assert.equal(invalid?.route, "__counterChanged");
    assert.ok(invalid?.error instanceof BridgeError && invalid.error.cause instanceof RangeError);
    assert.match(invalid.message, /bounded integer/);
    counter.update({ count: 2 });
  } finally {
    restore();
    client.dispose();
  }
});

test("an operation wait with a timeout cancels and resolves to timedOut", async () => {
  const bridge = freshBridge();
  bridge.view("counter", { state: { count: 1, label: "one" } });
  bridge.route("counterStartSave", payload => JSON.stringify({ contract, requestId: JSON.parse(String(payload)).requestId, kind: "accepted", terminal: null }));
  bridge.route("__runicOperationWait", () => new Promise<string>(() => {}));
  const cancels: string[] = [];
  const finished = new Set<string>();
  bridge.route("__runicOperationCancel", identity => {
    const requestId = JSON.parse(String(identity)).requestId as string;
    cancels.push(requestId);
    return JSON.stringify({ contract, requestId, kind: finished.has(requestId) ? "not-running" : "cancellation-requested" });
  });
  bridge.route("__runicOperationStatus", identity => {
    const requestId = JSON.parse(String(identity)).requestId as string;
    return JSON.stringify(finished.has(requestId)
      ? { contract, requestId, kind: "failed", error: { kind: "failed", message: "The operation failed.", detail: { type: "System.IO.IOException", message: "Locked." } } }
      : { contract, requestId, kind: "running" });
  });
  const client = await connectCounter();
  const { diagnostics, restore } = observe();
  try {
    const operation = await client.startOperation("Save", "slow", () => JSON.stringify({ requestId: "slow", input: null }), value => value);
    const status = await operation.wait({ timeout: 20 });
    assert.deepEqual(status, { contract, requestId: "slow", kind: "timedOut", cancellation: "cancellation-requested" });
    assert.deepEqual(cancels, ["slow"]);
    assert.equal(diagnostics.at(-1)?.code, "timedOut");
    await assert.rejects(operation.wait({ timeout: Number.NaN }), RangeError);

    finished.add("raced");
    const raced = await client.startOperation("Save", "raced", () => JSON.stringify({ requestId: "raced", input: null }), value => value);
    const terminal = await raced.wait({ timeout: 0 });
    assert.equal(terminal.kind, "failed", "An operation that finished before the cancel keeps its real terminal state.");
    assert.deepEqual(terminal.error?.detail, { type: "System.IO.IOException", message: "Locked." });

    bridge.route("__runicOperationCancel", () => { throw new Error("cancel lost"); });
    const unobserved = await client.startOperation("Save", "lost", () => JSON.stringify({ requestId: "lost", input: null }), value => value);
    assert.equal((await unobserved.wait({ timeout: 0 })).cancellation, "unobserved");
    assert.ok(diagnostics.some(diagnostic => diagnostic.code === "uncertain"
      && diagnostic.error instanceof BridgeOperationUncertainError && (diagnostic.error.cause as Error).message === "cancel lost"));
  } finally {
    restore();
    client.dispose();
  }
});
