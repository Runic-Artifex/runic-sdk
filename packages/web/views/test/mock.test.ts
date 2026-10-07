import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, BridgeOperationUncertainError, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { bridgeOperations, connectView, defineCollection, defineCollections, defineInteractions } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, mockTypedView, type MockBridge, type MockTypedCollection,
  type MockTypedInteraction, type MockTypedOperation, type MockTypedView } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
type State = { readonly title: string; readonly count: bigint; readonly rows: readonly Row[]; readonly canSave: boolean; readonly isSaveExecuting: boolean };
const decodeRow = (wire: unknown): Row => bridgeWire.object(wire, value => ({ id: bridgeWire.integer(value["id"], 0, 100000), label: bridgeWire.string(value["label"]) }));
const collections = defineCollections({ rows: defineCollection(decodeRow, row => String(row.id)) });
const hydrate = (wire: Record<string, unknown>): State => ({
  title: bridgeWire.string(wire["title"]), count: bridgeWire.bigint(wire["count"]), rows: bridgeWire.array(wire["rows"], decodeRow),
  canSave: bridgeWire.boolean(wire["canSave"]), isSaveExecuting: bridgeWire.boolean(wire["isSaveExecuting"]),
});
const contract = "Tests.NotesViewModel:ABC";

function fresh(scheduling: "immediate" | "manual" = "immediate"): MockBridge {
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  return installMockBridge(createMockBridge({ scheduling }));
}

// What a generated mockNotes() passes to the shared runtime.
interface NotesMock extends MockTypedView<Omit<State, "canSave" | "isSaveExecuting">, { save(): unknown; setTitle(): unknown; startSave(): unknown }> {
  readonly collections: { readonly rows: MockTypedCollection<Row> };
  readonly operations: { readonly save: readonly MockTypedOperation<void, string>[] };
  readonly interactions: { readonly confirm: MockTypedInteraction<string, boolean> };
}
function mockNotes(bridge: MockBridge, definition: Parameters<typeof mockTypedView>[2]): NotesMock {
  return mockTypedView(bridge, {
    kind: "notes", route: "notes", contract,
    fields: {
      title: { encode: value => value, decode: wire => bridgeWire.string(wire) },
      count: { encode: value => (value as bigint).toString(), decode: wire => bridgeWire.bigint(wire) },
      rows: { encode: value => value, decode: wire => bridgeWire.array(wire, decodeRow) },
    },
    defaults: { canSave: true, isSaveExecuting: false },
    checkedFields: ["title"],
    collections: { rows: { encode: value => value, decode: decodeRow, key: (item: Row) => String(item.id) } },
    setters: { setTitle: { route: "SetTitle", field: "title", read: argument => argument, checked: "WriteTitle" } },
    commands: { save: { route: "Save", available: "canSave" } },
    operations: { save: { member: "Save", encodeResult: value => value } },
    interactions: { confirm: { encodeInput: value => value, decodeOutput: wire => bridgeWire.boolean(wire) } },
  }, definition) as NotesMock;
}

const connect = () => connectView({ contract, route: "notes", mount: true, hydrate, collections, checkedFields: ["title"],
  interactions: defineInteractions({ confirm: { contract: `${contract}:interaction:Confirm`, decodeInput: value => bridgeWire.string(value), encodeOutput: value => value } }),
  operations: bridgeOperations });

test("manual scheduling delivers replies and pushes only when the test flushes", async () => {
  const bridge = fresh("manual");
  const notes = mockNotes(bridge, { state: { title: "a", count: 1n, rows: [] } });
  const pending = connect();
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(bridge.pending > 0, true);
  const client = await bridge.flushUntil(pending);
  assert.equal(client.snapshot.count, 1n);
  const seen: string[] = [];
  client.subscribe(state => seen.push(state.title));
  notes.update({ title: "b", count: 2n });
  assert.deepEqual(seen, ["a"]);
  await bridge.flush();
  assert.deepEqual(seen, ["a", "b"]);
  assert.equal(client.snapshot.count, 2n);
  const save = client.command("notesSave");
  await assert.rejects(bridge.flushUntil(new Promise(() => {})), /did not settle/);
  await bridge.flushUntil(save);
  client.dispose();
});

test("collection edits push delta frames that keep unchanged rows", async () => {
  fresh();
  const bridge = (globalThis as unknown as { __runicBridge: MockBridge }).__runicBridge;
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [{ id: 1, label: "one" }, { id: 2, label: "two" }] } });
  const client = await connect();
  const first = client.snapshot.rows[0];
  const frames: unknown[] = [];
  const host = globalThis as unknown as Record<string, (frame: unknown) => void>;
  const original = host["__notesChanged"]!;
  host["__notesChanged"] = frame => { frames.push(frame); original(frame); };
  notes.batch(() => {
    notes.collections.rows.add({ id: 3, label: "three" });
    notes.collections.rows.move("3", 0);
    notes.collections.rows.replace("2", { id: 2, label: "TWO" });
  });
  notes.collections.rows.remove("3");
  assert.equal(frames.length, 2);
  assert.equal((frames[0] as { __runicDelta: number }).__runicDelta, 1);
  assert.deepEqual(client.snapshot.rows, [{ id: 1, label: "one" }, { id: 2, label: "TWO" }]);
  assert.equal(client.snapshot.rows[0], first);
  assert.deepEqual(notes.collections.rows.keys, ["1", "2"]);
  host["__notesChanged"] = original;
  client.dispose();
});

test("failures reach the client as BridgeErrors and diagnostics", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  notes.failNext("save", { kind: "rejected", message: "Nope.", detail: { type: "System.InvalidOperationException", message: "Nope." } });
  await assert.rejects(client.command("notesSave"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.detail?.type === "System.InvalidOperationException");
  notes.failNext("save", { kind: "transport" });
  await assert.rejects(client.command("notesSave"), (error: unknown) => error instanceof BridgeError && error.kind === "failed");
  notes.update({ canSave: false } as never);
  await assert.rejects(client.command("notesSave"), /Save is unavailable/);

  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
  const report = globalThis.reportError;
  globalThis.reportError = () => {};
  try {
    notes.pushFailure({ message: "rows: row 1 has an empty key." });
    assert.equal(client.snapshot.title, "a");
    assert.match(diagnostics.map(diagnostic => diagnostic.message).join("\n"), /empty key/);
  } finally { stop(); globalThis.reportError = report; }
  client.dispose();
});

test("checked writes conflict on a stale baseline and setters apply handlers", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    setters: { setTitle: ((_state: unknown, value: string) => value === "" ? (() => { throw new Error("A title is required."); })() : { count: 9n }) as never } });
  const client = await connect();
  const baseline = client.fieldBaseline("title");
  const applied = await client.writeField("notesWriteTitle", JSON.stringify({ requestId: "r1", expectedVersion: baseline.version,
    expectedValue: baseline.value, value: "b" }), value => bridgeWire.string(value));
  assert.equal(applied.kind, "applied");
  assert.equal(client.snapshot.count, 9n);
  const stale = await client.writeField("notesWriteTitle", JSON.stringify({ requestId: "r2", expectedVersion: baseline.version,
    expectedValue: baseline.value, value: "c" }), value => bridgeWire.string(value));
  assert.equal(stale.kind, "conflict");
  const empty = await client.writeField("notesWriteTitle", JSON.stringify({ requestId: "r3", expectedVersion: client.fieldBaseline("title").version,
    expectedValue: "b", value: "" }), value => bridgeWire.string(value));
  assert.deepEqual(empty, { kind: "rejected", message: "A title is required." });
  assert.equal(notes.state.title, "b");
  client.dispose();
});

test("operations stay running until the test settles them and report executing state", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const operation = await client.startOperation<string>("Save", "request-1", () => "request-1", value => bridgeWire.string(value));
  assert.equal(client.snapshot.isSaveExecuting, true);
  assert.equal(notes.operations.save.length, 1);
  notes.operations.save[0]!.succeed("saved");
  assert.deepEqual(await operation.wait(), { contract: `${contract}:notes`, requestId: "request-1", kind: "succeeded", result: "saved" });
  assert.equal(client.snapshot.isSaveExecuting, false);

  const cancelled = await client.startOperation<string>("Save", "request-2", () => "request-2", value => bridgeWire.string(value));
  // Cancellation is cooperative: the request aborts the signal and the operation decides.
  assert.equal((await cancelled.cancel()).kind, "cancellation-requested");
  assert.equal(notes.operations.save[1]!.signal.aborted, true);
  assert.equal(notes.operations.save[1]!.kind, "running");
  notes.operations.save[1]!.cancel();
  assert.equal((await cancelled.wait()).kind, "cancelled");

  // A repeated request id observes the finished operation; reusing it for other input is rejected.
  const repeated = await client.startOperation<string>("Save", "request-1", () => "request-1", value => bridgeWire.string(value));
  const repeatedStatus = await repeated.status();
  assert.equal(repeatedStatus.kind === "succeeded" ? repeatedStatus.result : repeatedStatus.kind, "saved");
  assert.equal(notes.operations.save.length, 2);
  await assert.rejects(client.startOperation<string>("Save", "request-1", () => JSON.stringify({ requestId: "request-1", input: 1 }),
    value => bridgeWire.string(value)), (error: unknown) => error instanceof BridgeError && error.kind === "rejected");
  client.dispose();
});

test("operation handlers run on the virtual clock", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    operations: { save: (async () => { await bridge.sleep(1000); return { state: { title: "saved" }, result: "done" }; }) as never } });
  const client = await connect();
  const operation = await client.startOperation<string>("Save", "request-1", () => "request-1", value => bridgeWire.string(value));
  const completion = operation.wait();
  await bridge.advance(999);
  assert.equal(client.snapshot.title, "a");
  await bridge.advance(1);
  const completed = await completion;
  assert.equal(completed.kind === "succeeded" ? completed.result : completed.kind, "done");
  assert.equal(client.snapshot.title, "saved");
  assert.equal(bridge.now, 1000);
  client.dispose();
});

test("interactions reach the mounted client's handler and report its answer", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  assert.deepEqual(await notes.interactions.confirm.request("unhandled?"), { kind: "unhandled" });
  const inputs: string[] = [];
  const stop = client.interactions["confirm"]!.handle(((input: string) => { inputs.push(input); return input === "yes"; }) as never);
  await client.command("notesSave");
  assert.equal(notes.interactions.confirm.handled, true);
  assert.deepEqual(await notes.interactions.confirm.request("yes"), { kind: "answered", output: true });
  assert.deepEqual(await notes.interactions.confirm.request("no"), { kind: "answered", output: false });
  assert.deepEqual(inputs, ["yes", "no"]);
  stop();
  client.dispose();
});

test("checked writes are idempotent by request id and can commit with an error", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    setters: { setTitle: ((_state: unknown, value: string) => {
      if (value === "invalid") throw Object.assign(new Error("Saved, but the title failed validation."), { kind: "committed-with-error" });
    }) as never } });
  const client = await connectView({ contract, route: "notes", mount: false, hydrate, collections, checkedFields: ["title"] });
  const write = (requestId: string, value: string, baseline = client.fieldBaseline("title")) => client.writeField("notesWriteTitle",
    JSON.stringify({ requestId, expectedVersion: baseline.version, expectedValue: baseline.value, value }), value => bridgeWire.string(value));
  const baseline = client.fieldBaseline("title");
  const first = await write("r1", "b", baseline);
  assert.deepEqual(await write("r1", "b", baseline), first);
  const reused = await write("r1", "c", baseline);
  assert.equal(reused.kind, "conflict");
  assert.match(reused.kind === "conflict" ? reused.message : "", /already used/);
  const committed = await write("r2", "invalid");
  assert.equal(committed.kind, "committed-with-error");
  assert.equal(client.snapshot.title, "invalid");
  client.dispose();
});

test("manual scheduling coalesces full states like .NET delivery", async () => {
  const bridge = fresh("manual");
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await bridge.flushUntil(connect());
  const seen: string[] = [];
  client.subscribe(state => seen.push(state.title));
  notes.collections.rows.add({ id: 1, label: "one" });
  notes.update({ title: "b" });
  notes.update({ title: "c" });
  await bridge.flush();
  // The queued delta and the first state were superseded by the last full state.
  assert.deepEqual(seen, ["a", "c"]);
  assert.deepEqual(client.snapshot.rows, [{ id: 1, label: "one" }]);
  client.dispose();
});

test("more than 4096 collection changes are sent as a full state", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  const frames: unknown[] = [];
  const host = globalThis as unknown as Record<string, (frame: unknown) => void>;
  const push = host["__notesChanged"]!;
  host["__notesChanged"] = frame => { frames.push(frame); push(frame); };
  notes.batch(() => { for (let id = 0; id < 4097; id++) notes.collections.rows.add({ id, label: "row" }); });
  assert.equal(frames.length, 1);
  assert.equal((frames[0] as { __runicDelta?: number }).__runicDelta, undefined);
  assert.equal(client.snapshot.rows.length, 4097);
  host["__notesChanged"] = push;
  client.dispose();
});

// What runic-cswebui.js does when its WebUI connection drops: every call still
// in flight on that connection rejects (W130-056).
function loseCallsInFlight(bridge: MockBridge, held: (name: string) => boolean) {
  const inFlight: Array<(error: Error) => void> = [];
  const calls: string[] = [];
  (globalThis as unknown as { __runicBridge: unknown }).__runicBridge = {
    isConnected: () => bridge.isConnected(),
    call: (name: string, ...args: unknown[]) => {
      calls.push(name);
      return held(name) ? new Promise<string>((_, reject) => inFlight.push(reject)) : bridge.call(name, ...args);
    },
    onReconnect: (listener: () => void) => bridge.onReconnect!(listener),
  };
  const waitFor = async (condition: () => boolean) => {
    const deadline = Date.now() + 5_000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error(`Timed out; calls: ${calls.join(", ")}`);
      await new Promise(resolve => setTimeout(resolve, 5));
    }
  };
  return {
    calls, waitFor,
    held: () => inFlight.length,
    lose() {
      bridge.disconnect();
      for (const reject of inFlight.splice(0)) reject(new Error("The CS-WebUI connection was lost before the call completed."));
    },
  };
}

test("a command in flight when the connection is lost fails as disconnected", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const transport = loseCallsInFlight(bridge, name => name === "notesSave");
  const client = await connect();
  const save = client.command("notesSave");
  await transport.waitFor(() => transport.held() === 1);
  transport.lose();
  await assert.rejects(save, (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
  client.dispose();
});

test("an operation start in flight when the connection is lost has an uncertain outcome", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const transport = loseCallsInFlight(bridge, name => name === "notesStartSave");
  const client = await connect();
  // .NET may have admitted the operation before the reply was lost.
  const start = client.startOperation("Save", "request-1", () => JSON.stringify({ requestId: "request-1" }), value => value);
  await transport.waitFor(() => transport.held() === 1);
  transport.lose();
  await assert.rejects(start, (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-1");
  client.dispose();
});

test("interaction long polls in flight when the connection is lost stop without retrying", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const longPoll = (name: string) => name === "__runicInteractionWait" || name === "__runicInteractionControlWait";
  const transport = loseCallsInFlight(bridge, longPoll);
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  const reported: unknown[] = [];
  target.reportError = error => { reported.push(error); };
  try {
    const client = await connect();
    const stop = client.interactions["confirm"]!.handle((() => true) as never);
    await transport.waitFor(() => transport.held() === 2);
    transport.lose();
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(transport.calls.filter(longPoll).length, 2, "a stopped loop does not poll again");
    assert.deepEqual(reported, []);
    stop();
    client.dispose();
  } finally {
    target.reportError = previous;
  }
});

test("commands and interactions work again after a reconnect remounts the View", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  let holdLongPolls = true;
  const longPoll = (name: string) => name === "__runicInteractionWait" || name === "__runicInteractionControlWait";
  const transport = loseCallsInFlight(bridge, name => holdLongPolls && longPoll(name));
  const client = await connect();
  const inputs: string[] = [];
  const stop = client.interactions["confirm"]!.handle(((input: string) => { inputs.push(input); return input === "yes"; }) as never);
  await transport.waitFor(() => transport.held() === 2);
  transport.lose();
  await assert.rejects(client.command("notesSave"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");

  holdLongPolls = false;
  bridge.reconnect();
  await transport.waitFor(() => transport.calls.filter(name => name === "__runicInteractionWait").length >= 2);
  await client.command("notesSave");
  assert.deepEqual(await notes.interactions.confirm.request("yes"), { kind: "answered", output: true });
  assert.deepEqual(inputs, ["yes"]);
  stop();
  client.dispose();
});
