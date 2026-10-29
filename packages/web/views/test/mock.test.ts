import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, BridgeOperationUncertainError, createOperationController, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { bridgeOperations, connectView, defineCollection, defineCollections, defineInteractions } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge, type MockBridgeOptions, type MockTypedCollection,
  type MockTypedInteraction, type MockTypedOperation, type MockTypedView } from "../dist/mock.js";
import { mockTypedView } from "../dist/generated-mock.js";

type Row = { readonly id: number; readonly label: string };
type State = { readonly title: string; readonly count: bigint; readonly rows: readonly Row[]; readonly canSave: boolean; readonly isSaveExecuting: boolean };
const decodeRow = (wire: unknown): Row => bridgeWire.object(wire, value => ({ id: bridgeWire.integer(value["id"], 0, 100000), label: bridgeWire.string(value["label"]) }));
const collections = defineCollections({ rows: defineCollection(decodeRow, row => String(row.id)) });
const members = {
  title: bridgeWire.string, count: (wire: unknown) => bridgeWire.bigint(wire), rows: (wire: unknown) => bridgeWire.array(wire, decodeRow),
  canSave: bridgeWire.boolean, isSaveExecuting: bridgeWire.boolean,
};
const contract = "Tests.NotesViewModel:ABC";
const text = { decode: (value: unknown) => bridgeWire.string(value) };

function fresh(options: MockBridgeOptions = {}): MockBridge {
  return installMockBridge(createMockBridge(options));
}

async function until(condition: () => boolean, message: string): Promise<void> {
  for (let round = 0; !condition(); round++) {
    if (round > 1000) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 2));
  }
}

// What a generated mockNotes() passes to the shared runtime.
interface NotesMock extends MockTypedView<Omit<State, "canSave" | "isSaveExecuting">, {
  save(): unknown; setTitle(): unknown; writeTitle(): unknown; startSave(): unknown;
}> {
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
    setters: { setTitle: { member: "title", checked: "writeTitle" } },
    commands: { save: { member: "save", available: "canSave" } },
    operations: { save: { member: "save", encodeResult: value => value } },
    interactions: { confirm: { encodeInput: value => value, decodeOutput: wire => bridgeWire.boolean(wire) } },
  }, definition) as NotesMock;
}

const connect = () => connectView<State>({ contract, route: "notes", members, collections, checkedFields: ["title"],
  interactions: defineInteractions({ confirm: { contract: `${contract}:interaction:Confirm`, decodeInput: value => bridgeWire.string(value), encodeOutput: value => value } }),
  operations: bridgeOperations });

/** The frames the mock pushes while `run` runs. */
function frames(bridge: MockBridge): { readonly frames: Record<string, any>[]; stop(): void } {
  const seen: Record<string, any>[] = [];
  const stop = bridge.onMessage(message => {
    const push = JSON.parse(message) as Record<string, any>;
    if (push["kind"] === "frame") seen.push(push);
  });
  return { frames: seen, stop };
}

test("manual scheduling delivers replies and pushes only when the test flushes", async () => {
  const bridge = fresh({ scheduling: "manual" });
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
  const save = client.command("save");
  await assert.rejects(bridge.flushUntil(new Promise(() => {})), /did not settle/);
  await bridge.flushUntil(save);
  client.dispose();
});

test("collection edits push frames with indexed changes that keep unchanged rows", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [{ id: 1, label: "one" }, { id: 2, label: "two" }] } });
  const client = await connect();
  const first = client.snapshot.rows[0];
  const pushed = frames(bridge);
  notes.batch(() => {
    notes.collections.rows.add({ id: 3, label: "three" });
    notes.collections.rows.move("3", 0);
    notes.collections.rows.replace("2", { id: 2, label: "TWO" });
  });
  notes.collections.rows.remove("3");
  pushed.stop();
  assert.equal(pushed.frames.length, 2);
  assert.deepEqual(pushed.frames[0]!["changes"].map((change: { kind: string }) => change.kind), ["add", "move", "replace"]);
  assert.deepEqual(pushed.frames[1]!["changes"], [{ field: "rows", kind: "remove", index: 0, oldIndex: 0, keys: ["3"], items: [] }]);
  assert.equal(pushed.frames[1]!["baseRevision"], pushed.frames[0]!["revision"]);
  assert.deepEqual(client.snapshot.rows, [{ id: 1, label: "one" }, { id: 2, label: "TWO" }]);
  assert.equal(client.snapshot.rows[0], first);
  assert.deepEqual(notes.collections.rows.keys, ["1", "2"]);
  assert.equal(client.snapshot.title, "a");
  client.dispose();
});

test("failures reach the client as BridgeErrors and diagnostics", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  notes.failNext("save", { kind: "rejected", message: "Nope.", detail: { type: "System.InvalidOperationException", message: "Nope." } });
  await assert.rejects(client.command("save"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.detail?.type === "System.InvalidOperationException");
  notes.failNext("save", { kind: "transport" });
  await assert.rejects(client.command("save"), (error: unknown) => error instanceof BridgeError && error.kind === "failed");
  notes.update({ canSave: false } as never);
  assert.equal(client.snapshot.canSave, false);
  await assert.rejects(client.command("save"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && /save is unavailable/.test(error.message) && error.reason === "unavailable");

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
  const write = (requestId: string, value: string, baseline = client.fieldBaseline("title")) =>
    client.write("title", { requestId, expectedVersion: baseline.version, expectedValue: baseline.value, value }, wire => bridgeWire.string(wire));
  const baseline = client.fieldBaseline("title");
  assert.deepEqual(baseline, { value: "a", version: 0 });
  const applied = await write("r1", "b", baseline);
  assert.deepEqual(applied, { kind: "applied", snapshot: { value: "b", version: 1 } });
  assert.equal(client.snapshot.count, 9n);
  assert.deepEqual(client.fieldBaseline("title"), { value: "b", version: 1 });
  const stale = await write("r2", "c", baseline);
  assert.equal(stale.kind, "conflict");
  assert.deepEqual(stale.kind === "conflict" ? stale.incoming : undefined, { value: "b", version: 1 });
  assert.deepEqual(await write("r3", ""), { kind: "rejected", message: "A title is required." });
  assert.equal(notes.state.title, "b");
  // The setter handler runs for a plain set too.
  const state = await client.set("title", "d");
  assert.equal(state.title, "d");
  assert.equal(notes.state.title, "d");
  client.dispose();
});

test("operations stay running until the test settles them and report executing state", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const operation = await client.start("save", "request-1", undefined, text);
  assert.equal(client.snapshot.isSaveExecuting, true);
  assert.equal(notes.operations.save.length, 1);
  notes.operations.save[0]!.succeed("saved");
  assert.deepEqual(await operation.wait(), { contract, requestId: "request-1", kind: "succeeded", result: "saved" });
  assert.equal(client.snapshot.isSaveExecuting, false);

  const cancelled = await client.start("save", "request-2", undefined, text);
  // Cancellation is cooperative: the request aborts the signal and the operation decides.
  assert.equal((await cancelled.cancel()).kind, "cancellation-requested");
  assert.equal(notes.operations.save[1]!.signal.aborted, true);
  assert.equal(notes.operations.save[1]!.kind, "running");
  notes.operations.save[1]!.cancel();
  assert.equal((await cancelled.wait()).kind, "cancelled");

  // A repeated operation id observes the finished operation; reusing it for other input is an identity conflict.
  const repeated = await client.start("save", "request-1", undefined, text);
  const repeatedStatus = await repeated.status();
  assert.equal(repeatedStatus.kind === "succeeded" ? repeatedStatus.result : repeatedStatus.kind, "saved");
  assert.equal(notes.operations.save.length, 2);
  await assert.rejects(client.start("save", "request-1", 1, text),
    (error: unknown) => error instanceof BridgeError && error.kind === "rejected" && error.reason === "identity-conflict");
  client.dispose();
});

test("progress reports reach the watching client as running statuses", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const operation = await client.start("save", "request-1", undefined, text);
  const seen: unknown[] = [];
  operation.subscribe(status => seen.push(status.kind === "running" ? status.progress ?? "running" : status.kind));
  // The watch's reply first: a push that follows it may otherwise reach the page before the reply is handled.
  await until(() => seen.length > 0, "The client did not watch the operation.");
  const mock = notes.operations.save[0]!;
  mock.reportProgress(0.25);
  mock.reportProgress(7);
  assert.equal(mock.progress, 1);
  await until(() => seen.includes(1), "The progress did not reach the client.");
  mock.succeed("saved");
  await operation.wait();
  assert.deepEqual(seen, ["running", 0.25, 1, "succeeded"]);
  client.dispose();
});

test("an operation controller shows the progress the producer pushes to its watch", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const controller = createOperationController(() => client.start("save", "request-1", undefined, text));
  const result = controller.run();
  await until(() => bridge.calls.some(call => call.kind === "watch"), "The client did not watch the operation.");
  notes.operations.save[0]!.reportProgress(0.4);
  await until(() => controller.current.progress === 0.4, "The progress did not reach the controller.");
  notes.operations.save[0]!.succeed("saved");
  await result;
  assert.equal(controller.current.progress, undefined);
  assert.equal(controller.current.status?.kind, "succeeded");
  // The progress subscription and wait() share one watch.
  assert.equal(bridge.calls.filter(call => call.kind === "watch").length, 1);
  controller.dispose();
  client.dispose();
});

test("an expired operation reports expired, as a producer does after its retention runs out", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const operation = await client.start("save", "request-1", undefined, text);
  const mock = notes.operations.save[0]!;
  // A producer only expires a finished operation.
  assert.throws(() => mock.expire(), TypeError);
  mock.succeed("saved");
  mock.expire();
  assert.equal(mock.kind, "expired");
  assert.deepEqual(await operation.status(), { contract, requestId: "request-1", kind: "expired" });
  assert.deepEqual(await operation.wait(), { contract, requestId: "request-1", kind: "expired" });
  assert.equal((await operation.cancel()).kind, "expired");
  const raw = async (message: Record<string, unknown>) => JSON.parse(await bridge.request(JSON.stringify({
    route: "notes", member: "save", operation: "request-1", contract, ...message }))) as Record<string, any>;
  assert.deepEqual((await raw({ kind: "stream", cursor: 0 }))["page"], { kind: "expired", cursor: 0, completed: true, items: [] });
  // The id stays reserved: repeating the start answers expired, and other input is still an identity conflict.
  assert.deepEqual(await raw({ kind: "start" }), { ok: true, revision: notes.revision, admission: "expired" });
  await assert.rejects(client.start("save", "request-1", undefined, text),
    (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.reason === "expired");
  assert.equal((await raw({ kind: "start", input: 1 }))["reason"], "identity-conflict");
  await assert.rejects(client.recover("save", "request-1", text), BridgeOperationUncertainError);
  assert.equal(notes.operations.save.length, 1);
  client.dispose();
});

test("a forgotten operation reports unknown, as after the window restarted", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, operations: { save: "manual" } });
  const client = await connect();
  const operation = await client.start("save", "request-1", undefined, text);
  const completion = operation.wait();
  const mock = notes.operations.save[0]!;
  mock.forget();
  assert.equal(mock.kind, "unknown");
  assert.equal(mock.signal.aborted, true);
  assert.equal(client.snapshot.isSaveExecuting, false);
  assert.deepEqual(await completion, { contract, requestId: "request-1", kind: "unknown" });
  assert.deepEqual(await operation.status(), { contract, requestId: "request-1", kind: "unknown" });
  assert.equal((await operation.cancel()).kind, "unknown");
  // A late outcome of the forgotten operation changes nothing.
  mock.succeed("late");
  assert.equal(mock.kind, "unknown");
  await assert.rejects(client.recover("save", "request-1", text), BridgeOperationUncertainError);
  // The id is free again: a start begins a new operation.
  const again = await client.start("save", "request-1", undefined, text);
  assert.equal(notes.operations.save.length, 1);
  notes.operations.save[0]!.succeed("saved");
  const status = await again.wait();
  assert.equal(status.kind === "succeeded" ? status.result : status.kind, "saved");
  // Forgetting a finished operation works the same way.
  notes.operations.save[0]!.forget();
  assert.equal((await again.status()).kind, "unknown");
  client.dispose();
});

test("operation handlers run on the virtual clock", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    operations: { save: (async () => { await bridge.sleep(1000); return { state: { title: "saved" }, result: "done" }; }) as never } });
  const client = await connect();
  const operation = await client.start("save", "request-1", undefined, text);
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

test("interactions reach the open presentation's handler and report its answer", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  assert.deepEqual(await notes.interactions.confirm.request("unhandled?"), { kind: "unhandled" });
  const inputs: string[] = [];
  const stop = client.interactions["confirm"]!.handle(((input: string) => { inputs.push(input); return input === "yes"; }) as never);
  // A command runs after the presentation's handlers are registered.
  await client.command("save");
  assert.equal(notes.interactions.confirm.handled, true);
  assert.deepEqual(await notes.interactions.confirm.request("yes"), { kind: "answered", output: true });
  assert.deepEqual(await notes.interactions.confirm.request("no"), { kind: "answered", output: false });
  assert.deepEqual(inputs, ["yes", "no"]);
  stop();
  await until(() => !notes.interactions.confirm.handled, "The handler stayed registered.");
  client.dispose();
});

test("checked writes are idempotent by request id and can commit with an error", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    setters: { setTitle: ((_state: unknown, value: string) => {
      if (value === "invalid") throw Object.assign(new Error("Saved, but the title failed validation."), { kind: "committed-with-error" });
    }) as never } });
  const client = await connectView<State>({ contract, route: "notes", members, collections, checkedFields: ["title"] });
  const write = (requestId: string, value: string, baseline = client.fieldBaseline("title")) => client.write("title",
    { requestId, expectedVersion: baseline.version, expectedValue: baseline.value, value }, wire => bridgeWire.string(wire));
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

test("manual scheduling delivers each queued frame in order when the test flushes", async () => {
  const bridge = fresh({ scheduling: "manual" });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await bridge.flushUntil(connect());
  const seen: string[] = [];
  client.subscribe(state => seen.push(`${state.title}:${state.rows.length}`));
  notes.collections.rows.add({ id: 1, label: "one" });
  notes.update({ title: "b" });
  notes.update({ title: "c" });
  assert.deepEqual(seen, ["a:0"]);
  await bridge.flush();
  // Protocol 4 has no full-state coalescing: each frame continues the previous one.
  assert.deepEqual(seen, ["a:0", "a:1", "b:1", "c:1"]);
  assert.deepEqual(client.snapshot.rows, [{ id: 1, label: "one" }]);
  assert.equal(bridge.calls.filter(call => call.kind === "snapshot").length, 0);
  client.dispose();
});

test("a unit of more than 4096 changes publishes the collection as a set of its field", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  const pushed = frames(bridge);
  notes.batch(() => { for (let id = 0; id < 4097; id++) notes.collections.rows.add({ id, label: "row" }); });
  pushed.stop();
  assert.equal(pushed.frames.length, 1);
  const changes = pushed.frames[0]!["changes"] as { field: string; kind: string; value: unknown[] }[];
  assert.deepEqual(changes.map(change => [change.field, change.kind, change.value.length]), [["rows", "set", 4097]]);
  assert.equal(client.snapshot.rows.length, 4097);
  client.dispose();
});

test("a command in flight when the connection is lost fails as disconnected", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] }, commands: { save: (() => new Promise(() => {})) as never } });
  const client = await connect();
  const save = client.command("save");
  await until(() => bridge.calls.some(call => call.kind === "command"), "The command was not sent.");
  bridge.disconnect();
  await assert.rejects(save, (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
  client.dispose();
});

test("an operation start in flight when the connection is lost has an uncertain outcome", async () => {
  const bridge = fresh();
  mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  // The producer may have admitted the operation before the reply was lost.
  bridge.intercept(({ kind }) => kind === "start" ? new Promise(() => {}) : undefined);
  const client = await connect();
  const start = client.start("save", "request-1", undefined, text);
  await until(() => bridge.calls.some(call => call.kind === "start"), "The start was not sent.");
  bridge.disconnect();
  // The error names the View's contract and route apart, and is deliberately not a BridgeError.
  await assert.rejects(start, (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-1"
    && error.contract === contract && error.route === "notes" && !(error instanceof BridgeError));
  client.dispose();
});

test("BridgeOperationUncertainError takes its identity in an options object", () => {
  const cause = new Error("lost");
  const error = new BridgeOperationUncertainError("The outcome is unknown.", { contract, requestId: "r", route: "notes", cause });
  assert.deepEqual([error.message, error.contract, error.requestId, error.route, error.cause], ["The outcome is unknown.", contract, "r", "notes", cause]);
  assert.equal(new BridgeOperationUncertainError("m", { contract, requestId: "r" }).route, undefined);
});

// A producer releases a closed connection's presentations; the client says hello with a new session and opens its
// presentations again with the same ids.
test("commands and interactions work again after a reconnect opens the View again", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  const inputs: string[] = [];
  const stop = client.interactions["confirm"]!.handle(((input: string) => { inputs.push(input); return input === "yes"; }) as never);
  await client.command("save");
  assert.equal(notes.interactions.confirm.handled, true);
  bridge.disconnect();
  assert.equal(notes.interactions.confirm.handled, false);
  await assert.rejects(client.command("save"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");

  bridge.reconnect();
  await until(() => notes.interactions.confirm.handled, "The handler was not registered on the new connection.");
  notes.update({ title: "b" });
  await client.command("save");
  assert.equal(client.snapshot.title, "b");
  assert.deepEqual(await notes.interactions.confirm.request("yes"), { kind: "answered", output: true });
  assert.deepEqual(inputs, ["yes"]);
  const sessions = bridge.calls.filter(call => call.kind === "hello").map(call => call.message["session"]);
  assert.equal(sessions.length, 2);
  assert.notEqual(sessions[0], sessions[1]);
  const presentations = bridge.calls.filter(call => call.kind === "open").map(call => call.message["presentation"]);
  assert.equal(presentations.length, 2);
  assert.equal(presentations[0], presentations[1]);
  stop();
  client.dispose();
});

// What a generated mock of a [RunicCommandKey] command passes to the shared runtime.
test("a keyed command's mock resolves the key before availability and rejects with unresolved-key", async () => {
  const bridge = fresh();
  const items = new Set(["a", "b"]);
  mockTypedView(bridge, {
    kind: "items", route: "items", contract: "Tests.ItemsViewModel:ABC", fields: {}, defaults: {},
    commands: { remove: { member: "remove", decodeInput: wire => bridgeWire.string(wire) } },
  }, {
    state: {},
    resolveKey: { remove: (_state: unknown, id: string) => items.has(id) },
    canExecute: { remove: (_state: unknown, id: string) => id !== "b" },
    commands: { remove: (_state: unknown, id: string) => { items.delete(id); } },
  });
  const client = await connectView<Record<string, never>>({ contract: "Tests.ItemsViewModel:ABC", route: "items", members: {} });
  assert.equal(await client.can("remove", "a"), true);
  assert.equal(await client.can("remove", "b"), false);
  assert.equal(await client.can("remove", "z"), false);
  await client.command("remove", "a");
  assert.equal(items.has("a"), false);
  await assert.rejects(client.command("remove", "a"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.reason === "unresolved-key" && error.message === "Remove has no object for this key.");
  // An unresolved key is reported before availability, as a producer resolves the key first.
  items.delete("b");
  await assert.rejects(client.command("remove", "b"), (error: unknown) => error instanceof BridgeError && error.reason === "unresolved-key");
  items.add("b");
  await assert.rejects(client.command("remove", "b"), (error: unknown) => error instanceof BridgeError && error.reason === "unavailable");
  // An injected failure carries a reason.
  bridge.failNext({ route: "items", kind: "command", member: "remove" }, { kind: "rejected", reason: "unresolved-key", message: "Gone." });
  await assert.rejects(client.command("remove", "b"), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.reason === "unresolved-key" && error.message === "Gone.");
  client.dispose();
});

test("a rejected operation start keeps the admission reason as BridgeError.reason", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  let reason = "";
  bridge.intercept(({ kind }) => kind === "start" ? { ok: true, revision: notes.revision, admission: "rejected", reason } : undefined);
  // The reason vocabulary is open: a reason the client does not know is kept as sent.
  for (reason of ["unavailable", "unresolved-key", "capacity", "identity-conflict", "owner-closing", "a-later-reason"]) {
    await assert.rejects(client.start("save", `request-${reason}`, undefined, text), (error: unknown) =>
      error instanceof BridgeError && error.kind === "rejected" && error.reason === reason && error.message.includes(reason));
  }
  client.dispose();
});

test("an injected rejection of a start answers a rejected admission with its reason", async () => {
  const bridge = fresh();
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  notes.failNext("startSave", { kind: "rejected", reason: "capacity", message: "Too many." });
  await assert.rejects(client.start("save", "request-1", undefined, text), (error: unknown) =>
    error instanceof BridgeError && error.kind === "rejected" && error.reason === "capacity");
  assert.equal(notes.operations.save.length, 0);
  client.dispose();
});

/* ------------------------------------------------------------------ latency and reordering */

test("latency in milliseconds delays requests, replies and pushes on real timers", async () => {
  const bridge = fresh({ latency: 15 });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const started = performance.now();
  const client = await connect();
  // hello and open go out together (README "Handshake"): one request and one reply.
  assert.ok(performance.now() - started >= 25, "The connection did not wait for the latency.");
  notes.update({ title: "b" });
  assert.equal(client.snapshot.title, "a");
  await until(() => client.snapshot.title === "b", "The push did not arrive.");
  client.dispose();
});

test("a latency range keeps replies and pushes in the order the mock sent them", async () => {
  const bridge = fresh({ latency: { min: 0, max: 12 } });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] },
    commands: { save: ((state: Record<string, unknown>) => ({ count: String(BigInt(state.count as never) + 1n) })) as never } });
  const client = await connect();
  const pushed = frames(bridge);
  for (let id = 0; id < 20; id++) notes.collections.rows.add({ id, label: `row ${id}` });
  const saves = Array.from({ length: 5 }, () => client.command("save"));
  for (const state of await Promise.all(saves)) assert.ok(state.count >= 1n);
  await until(() => client.snapshot.rows.length === 20, "The frames did not arrive.");
  pushed.stop();
  // Each frame continues the previous one, so the client never needed a snapshot to recover.
  for (let index = 1; index < pushed.frames.length; index++)
    assert.equal(pushed.frames[index]!["baseRevision"], pushed.frames[index - 1]!["revision"]);
  assert.equal(client.snapshot.count, 5n);
  assert.equal(bridge.calls.filter(call => call.kind === "snapshot").length, 0);
  client.dispose();
});

test("\"slow\" latency makes key-by-key typing race its echoes, and the overlay keeps the latest value", async () => {
  const bridge = fresh({ latency: "slow" });
  const notes = mockNotes(bridge, { state: { title: "", count: 0n, rows: [] } });
  const client = await connect();
  const seen: string[] = [];
  client.subscribe(state => seen.push(state.title));
  const typing: Promise<unknown>[] = [];
  for (const value of ["h", "he", "hel", "hell", "hello"]) {
    typing.push(client.set("title", value));
    assert.equal(client.snapshot.title, value);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  await Promise.all(typing);
  assert.equal(client.snapshot.title, "hello");
  assert.equal(notes.state.title, "hello");
  // The page never showed an older value after a newer one.
  assert.deepEqual(seen, ["", "h", "he", "hel", "hell", "hello"]);
  client.dispose();
});

test("latency with manual scheduling runs on the virtual clock", async () => {
  const bridge = fresh({ scheduling: "manual", latency: 50 });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const connected: { client?: Awaited<ReturnType<typeof connect>> } = {};
  void connect().then(client => { connected.client = client; });
  // hello and open go out together, and each takes 50 ms to the producer and 50 ms back.
  await bridge.advance(99);
  assert.equal(connected.client, undefined);
  await bridge.advance(1);
  const client = connected.client!;
  assert.ok(client);
  assert.equal(bridge.now, 100);
  notes.update({ title: "b" });
  await bridge.advance(49);
  assert.equal(client.snapshot.title, "a");
  await bridge.advance(1);
  assert.equal(client.snapshot.title, "b");
  await assert.rejects(bridge.flushUntil(client.command("save")), /did not settle/);
  client.dispose();
});

test("a latency range with manual scheduling keeps pushes in order on the virtual clock", async () => {
  const bridge = fresh({ scheduling: "manual", latency: { min: 10, max: 100 } });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const pending = connect();
  await bridge.advance(400);
  const client = await pending;
  const seen: string[] = [];
  client.subscribe(state => seen.push(state.title));
  for (const title of ["b", "c", "d", "e", "f"]) notes.update({ title });
  await bridge.advance(100);
  assert.deepEqual(seen, ["a", "b", "c", "d", "e", "f"]);
  assert.equal(bridge.calls.filter(call => call.kind === "snapshot").length, 0);
  client.dispose();
});

test("an invalid latency is a RangeError", () => {
  for (const latency of [-1, Number.NaN, Number.POSITIVE_INFINITY, { min: 5, max: 1 }, { min: -1, max: 1 }])
    assert.throws(() => createMockBridge({ latency }), RangeError, JSON.stringify(latency));
});

test("reorderRequests admits requests out of order, and a stale edit answers superseded", async () => {
  const bridge = fresh({ reorderRequests: true });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await connect();
  const order: unknown[] = [];
  bridge.intercept(({ kind, message }) => { if (kind === "set") order.push(message["edit"]); return undefined; });
  const first = client.set("title", "x");
  const second = client.set("title", "xy");
  assert.equal(client.snapshot.title, "xy");
  await Promise.all([first, second]);
  // The newer edit was admitted first, so the older one did not overwrite it.
  assert.deepEqual(order, [2, 1]);
  assert.equal(notes.state.title, "xy");
  assert.equal(client.snapshot.title, "xy");
  const edits = bridge.calls.filter(call => call.kind === "set").map(call => call.message["edit"]);
  assert.deepEqual(edits, [1, 2]);
  client.dispose();
});

test("reorderRequests with manual scheduling admits requests due together newest first", async () => {
  const bridge = fresh({ scheduling: "manual", reorderRequests: true });
  const notes = mockNotes(bridge, { state: { title: "a", count: 0n, rows: [] } });
  const client = await bridge.flushUntil(connect());
  const order: unknown[] = [];
  bridge.intercept(({ kind, message }) => { if (kind === "set") order.push(message["value"]); return undefined; });
  const sets = Promise.all(["x", "xy", "xyz"].map(value => client.set("title", value)));
  await bridge.flushUntil(sets);
  assert.deepEqual(order, ["xyz", "xy", "x"]);
  assert.equal(notes.state.title, "xyz");
  assert.equal(client.snapshot.title, "xyz");
  client.dispose();
});
