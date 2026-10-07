// W130-029: declared failures resolve a BridgeOutcome; unexpected failures
// still reject with BridgeError. Generated clients do not call these APIs
// until the generator emits failure codecs, so the tests call them directly.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import {
  BridgeError, BridgeOperationUncertainError, bridgeFailure, bridgeSuccess, createCommandController, isBridgeOutcome, matchCase,
  onBridgeDiagnostic, type BridgeDiagnostic, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome, type CommandState,
} from "../dist/index.js";
import { bridgeOperations, connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import {
  createMockBridge, installMockBridge, mockFailure, mockTypedView, type MockBridge, type MockFailureOf, type MockTypedOperation,
  type MockTypedView,
} from "../dist/mock.js";

type SaveFailure = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
type EditorState = { readonly title: string };
const contract = "Tests.Editor:fixture";
const statusContract = `${contract}:editor`;

const decodeFailure = (wire: unknown): SaveFailure => bridgeWire.object(wire, value => {
  switch (value["$case"]) {
    case "titleRequired": return { $case: "titleRequired" };
    case "titleTaken": return { $case: "titleTaken", existingTitle: bridgeWire.string(value["existingTitle"]) };
    default: throw new TypeError("Unknown SaveFailure case.");
  }
});
const hydrate = (wire: Record<string, unknown>): EditorState => ({ title: bridgeWire.string(wire["title"]) });

function fresh(): MockBridge {
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  return installMockBridge(createMockBridge());
}

const connect = () => connectView({ contract, route: "editor", mount: false, hydrate, operations: bridgeOperations });

function captureReports() {
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  const reported: unknown[] = [];
  target.reportError = error => { reported.push(error); };
  return { reported, restore: () => { target.reportError = previous; } };
}

// Compile-time checks: `expectType<T>()(value)` fails to compile unless value is exactly T.
type Exact<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
const expectType = <T>() => <V>(_value: V, ..._exact: Exact<T, V> extends true ? [] : [never]) => undefined;

test("outcomes carry a registered, non-enumerable brand", () => {
  const success = bridgeSuccess(1);
  const failure = bridgeFailure<SaveFailure>({ $case: "titleRequired" });
  assert.equal(isBridgeOutcome(success) && isBridgeOutcome(failure), true);
  assert.deepEqual({ ...success }, { ok: true, value: 1 });
  assert.equal(isBridgeOutcome({ ...failure }), false, "a spread outcome is plain data");
  assert.equal(isBridgeOutcome(JSON.parse(JSON.stringify(failure))), false, "a JSON round-trip is plain data");
  assert.equal(isBridgeOutcome({ ok: false, failure: 1 }), false);
  assert.equal(Object.isFrozen(success), true);
  assert.equal((success as unknown as Record<symbol, unknown>)[Symbol.for("runic.bridgeOutcome")], true);
});

test("matchCase calls the handler of the case and infers the union of handler results", () => {
  const failure = { $case: "titleTaken", existingTitle: "Todo" } as SaveFailure;
  const result = matchCase(failure, {
    titleRequired: () => 0,
    titleTaken: value => value.existingTitle,
  });
  expectType<string | number>()(result);
  assert.equal(result, "Todo");
  // @ts-expect-error a handler for every case is required
  assert.throws(() => matchCase(failure, { titleRequired: () => 0 }), TypeError);
  assert.equal(matchCase({ $case: "titleRequired" } as SaveFailure, { titleRequired: () => true, titleTaken: () => false }), true);
});

test("commandOutcome resolves a declared failure with the state and rejects other failures", async () => {
  const bridge = fresh();
  let failure: unknown = { $case: "titleTaken", existingTitle: "Todo" };
  bridge.view("editor", {
    state: { title: "" },
    routes: {
      Save: () => { throw mockFailure(failure); },
      Rename: () => ({ title: "renamed" }),
      Broken: () => { throw new Error("disk full"); },
    },
  });
  const client = await connect();
  const outcome = await client.commandOutcome("editorSave", decodeFailure);
  expectType<BridgeOutcome<EditorState, SaveFailure>>()(outcome);
  assert.equal(isBridgeOutcome(outcome), true);
  assert.deepEqual(outcome, bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  const renamed = await client.commandOutcome("editorRename", decodeFailure);
  assert.equal(renamed.ok && renamed.value.title, "renamed");
  assert.equal(client.snapshot.title, "renamed");
  await assert.rejects(client.commandOutcome("editorBroken", decodeFailure),
    (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.message === "disk full");
  failure = { $case: "unknown" };
  await assert.rejects(client.commandOutcome("editorSave", decodeFailure),
    (error: unknown) => error instanceof BridgeError && error.kind === "failed" && /invalid failure/.test(error.message));
  client.dispose();
});

test("a declared failure without a state resolves and reports the missing state", async () => {
  const bridge = fresh();
  bridge.view("editor", { state: { title: "" } });
  bridge.route("editorSave", () => JSON.stringify({ ok: false, state: null, error: {
    kind: "domain-failed", message: "Save failed. The updated state could not be sent: rows has a duplicate key.",
    failure: { $case: "titleRequired" } } }));
  // A detached .NET Bridge replies without state but with the plain message.
  bridge.route("editorDetached", () => JSON.stringify({ ok: false, state: null, error: {
    kind: "domain-failed", message: "Save failed.", failure: { $case: "titleRequired" } } }));
  const client = await connect();
  const reports = captureReports();
  try {
    assert.deepEqual(await client.commandOutcome("editorSave", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
    assert.equal(reports.reported.length, 1);
    const reported = reports.reported[0];
    assert.ok(reported instanceof BridgeError && reported.kind === "failed" && reported.route === "editorSave"
      && /could not be sent/.test(reported.message));
    assert.deepEqual(await client.commandOutcome("editorDetached", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
    assert.equal(reports.reported.length, 1, "a reply from a detached Bridge is not a key failure");
  } finally {
    reports.restore();
    client.dispose();
  }
});

test("an unknown or undeclared error kind is a failed BridgeError", async () => {
  const bridge = fresh();
  bridge.view("editor", { state: { title: "" } });
  const reply = (kind: string) => JSON.stringify({ ok: false, state: { revision: 1, title: "" }, error: { kind, message: "Save failed.", failure: {} } });
  bridge.route("editorSave", () => reply("domain-failed"));
  bridge.route("editorFuture", () => reply("throttled"));
  const client = await connect();
  for (const route of ["editorSave", "editorFuture"])
    await assert.rejects(client.command(route), (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.message === "Save failed.");
  client.dispose();
});

test("a snapshot reply from another protocol version is reported once", async () => {
  const bridge = fresh();
  let protocol = 1;
  bridge.view("editor", { state: { title: "" } });
  bridge.view("other", { state: { title: "" } });
  for (const route of ["editor", "other"])
    bridge.route(`${route}Snapshot`, () => JSON.stringify({ ok: true, state: { revision: 1, title: "" }, error: null, protocol }));
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
  try {
    const first = await connect();
    const second = await connectView({ contract, route: "other", mount: false, hydrate });
    const reported = diagnostics.filter(diagnostic => diagnostic.code === "protocol");
    assert.equal(reported.length, 1);
    assert.match(reported[0]!.message, /protocol 1, but this client was built for protocol 2/);
    first.dispose();
    second.dispose();
    const quiet = fresh();
    quiet.view("editor", { state: { title: "" } });
    diagnostics.length = 0;
    protocol = 2;
    const current = await connect();
    assert.equal(diagnostics.some(diagnostic => diagnostic.code === "protocol"), false);
    current.dispose();
  } finally {
    stop();
  }
});

// Starts an operation whose wait route answers `status`.
async function operationWith(status: Record<string, unknown>, options: { declared?: boolean; stream?: boolean; decode?: (value: unknown) => unknown } = {}) {
  const bridge = fresh();
  bridge.view("editor", { state: { title: "" } });
  bridge.route("editorStartSave", () => JSON.stringify({ kind: "accepted", status: "running", reason: null, terminal: null }));
  bridge.route("__runicOperationWait", () => JSON.stringify({ contract: statusContract, requestId: "save-1", ...status }));
  bridge.route("__runicOperationCancel", () => JSON.stringify({ contract: statusContract, requestId: "save-1", kind: "cancellation-requested" }));
  const client = await connect();
  const decode = options.decode ?? ((value: unknown) => bridgeWire.string(value));
  const operation = options.declared === false
    ? await client.startOperation("Save", "save-1", () => "save-1", decode, options.stream ?? false)
    : await client.startOperation("Save", "save-1", () => "save-1", decode, options.stream ?? false, decodeFailure);
  return { bridge, client, operation };
}

test("outcome() follows the terminal status", async () => {
  const rejectsWith = (kind: string, message?: RegExp) => (error: unknown) =>
    error instanceof BridgeError && error.kind === kind && (message === undefined || message.test(error.message));

  const succeeded = await operationWith({ kind: "succeeded", result: "saved" });
  assert.deepEqual(await succeeded.operation.outcome(), bridgeSuccess("saved"));
  const empty = await operationWith({ kind: "succeeded" });
  assert.deepEqual(await empty.operation.outcome(), bridgeSuccess(undefined));

  const delivery = { kind: "result-too-large", message: "The operation result exceeds the 16 byte retention limit." };
  const lost = await operationWith({ kind: "succeeded", delivery });
  await assert.rejects(lost.operation.outcome(), (error: unknown) => rejectsWith("failed", /16 byte/)(error)
    && ((error as BridgeError).cause as BridgeOperationStatus).kind === "succeeded");
  const stream = await operationWith({ kind: "succeeded", delivery, stream: true }, { stream: true });
  assert.deepEqual(await stream.operation.outcome(), bridgeSuccess(undefined));

  const declared = await operationWith({ kind: "domain-failed", failure: { $case: "titleTaken", existingTitle: "Todo" } });
  assert.deepEqual(await declared.operation.outcome(), bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  const dropped = await operationWith({ kind: "domain-failed", delivery });
  await assert.rejects(dropped.operation.outcome(), rejectsWith("failed", /16 byte/));
  const undecodable = await operationWith({ kind: "domain-failed", failure: { $case: "other" } });
  await assert.rejects(undecodable.operation.outcome(), rejectsWith("failed", /failure is invalid/));

  const failed = await operationWith({ kind: "failed", error: { kind: "failed", message: "The operation failed.",
    detail: { type: "System.InvalidOperationException", message: "disk full" } } });
  await assert.rejects(failed.operation.outcome(), (error: unknown) => rejectsWith("failed", /operation failed/)(error)
    && (error as BridgeError).detail?.message === "disk full");
  const cancelled = await operationWith({ kind: "cancelled" });
  await assert.rejects(cancelled.operation.outcome(), rejectsWith("cancelled"));
  for (const kind of ["expired", "unknown"]) {
    const uncertain = await operationWith({ kind });
    await assert.rejects(uncertain.operation.outcome(), (error: unknown) => error instanceof BridgeOperationUncertainError
      && error.requestId === "save-1");
  }

  const hanging = await operationWith({ kind: "running" });
  hanging.bridge.route("__runicOperationWait", () => new Promise<string>(() => {}));
  await assert.rejects(hanging.operation.outcome({ timeout: 0 }), (error: unknown) => rejectsWith("timeout")(error)
    && ((error as BridgeError).cause as { readonly cancellation?: string }).cancellation === "cancellation-requested");
});

test("an operation without a declared failure reports domain-failed as failed", async () => {
  const { operation, client } = await operationWith({ kind: "domain-failed", failure: { $case: "titleRequired" }, stream: true },
    { declared: false, stream: true });
  const status = await operation.wait();
  assert.equal(status.kind, "failed");
  assert.equal(status.kind === "failed" && status.error.message, "The operation failed.");
  assert.equal("failure" in status, false);
  await assert.rejects(operation.outcome(), (error: unknown) => error instanceof BridgeError && error.kind === "failed");
  client.dispose();
});

test("the status union narrows by kind and hides domain-failed for undeclared operations", () => {
  const read = (status: BridgeOperationStatus<number, SaveFailure>) => {
    switch (status.kind) {
      case "succeeded": return status.result;
      case "domain-failed": return status.failure?.$case;
      case "failed": return status.error.message;
      case "timedOut": return status.cancellation;
      case "running": case "cancelled": case "expired": case "unknown": return status.kind;
      default: { const exhaustive: never = status; return exhaustive; }
    }
  };
  const undeclared = (status: BridgeOperationStatus<number>) => {
    // @ts-expect-error an operation without a declared failure has no domain-failed status
    if (status.kind === "domain-failed") return status;
    return status.kind;
  };
  expectType<BridgeOperation<void, never>>()(undefined as unknown as BridgeOperation);
  assert.equal(read({ contract, requestId: "1", kind: "domain-failed", failure: { $case: "titleRequired" } }), "titleRequired");
  assert.equal(undeclared({ contract, requestId: "1", kind: "cancelled" }), "cancelled");
});

test("a command controller keeps declared failures apart from errors", async () => {
  const settle: ((value: BridgeOutcome<number, SaveFailure> | Error) => void)[] = [];
  const command = createCommandController(() => new Promise<BridgeOutcome<number, SaveFailure>>((resolve, reject) => {
    settle.push(value => value instanceof Error ? reject(value) : resolve(value));
  }));
  expectType<CommandState<SaveFailure>>()(command.current);
  const plain = createCommandController(() => Promise.resolve(1));
  expectType<CommandState<never>>()(plain.current);
  const optional = createCommandController((client?: { save(): Promise<BridgeOutcome<number, SaveFailure>> }) => client?.save());
  expectType<SaveFailure | undefined>()(optional.current.failure);

  // Read through a function: assert.deepEqual narrows a property it was given.
  const current = (): CommandState<SaveFailure> => command.current;
  const first = command.run();
  settle[0]!(bridgeFailure({ $case: "titleRequired" }));
  assert.deepEqual(await first, bridgeFailure({ $case: "titleRequired" }));
  assert.deepEqual(current(), { pending: false, error: undefined, failure: { $case: "titleRequired" } });

  const second = command.run();
  assert.equal(current().failure, undefined, "starting a run clears the failure");
  settle[1]!(new Error("broken"));
  assert.equal(await second, undefined);
  assert.equal((current().error as Error).message, "broken");
  assert.equal(current().failure, undefined);
  command.reset();
  assert.deepEqual(current(), { pending: false, error: undefined, failure: undefined });

  // The latest run wins: a superseded run publishes neither error nor failure.
  const stale = command.run();
  const latest = command.run();
  settle[2]!(bridgeFailure({ $case: "titleRequired" }));
  await stale;
  assert.equal(current().failure, undefined);
  settle[3]!(bridgeSuccess(2));
  assert.deepEqual(await latest, bridgeSuccess(2));
  assert.deepEqual(current(), { pending: false, error: undefined, failure: undefined });
  const staleFailure = command.run();
  const winner = command.run();
  settle[5]!(bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  await winner;
  settle[4]!(new Error("stale"));
  await staleFailure;
  assert.deepEqual(current(), { pending: false, error: undefined, failure: { $case: "titleTaken", existingTitle: "Todo" } });

  // reset() supersedes a run in flight, so its late failure does not reappear.
  const inFlight = command.run();
  command.reset();
  assert.equal(current().pending, true, "the run is still in flight");
  settle[6]!(bridgeFailure({ $case: "titleRequired" }));
  await inFlight;
  assert.deepEqual(current(), { pending: false, error: undefined, failure: undefined });
});

test("the mock Bridge answers declared failures for commands, operations and streams", async () => {
  const bridge = fresh();
  const editor = bridge.view("editor", {
    state: { title: "" },
    routes: { Save: () => { throw mockFailure({ $case: "titleRequired" }); } },
    operations: {
      Save: () => { throw mockFailure({ $case: "titleTaken", existingTitle: "Todo" }); },
      Publish: async (_state, _input, operation) => { operation.emit("one"); throw mockFailure({ $case: "titleRequired" }); },
      Hold: "manual",
    },
    streams: ["Publish"],
  });
  const client = await connect();
  const snapshot = JSON.parse(await bridge.call("editorSnapshot")) as Record<string, unknown>;
  assert.equal(snapshot["protocol"], 2);
  const saved = JSON.parse(await bridge.call("editorSave")) as Record<string, unknown>;
  assert.deepEqual(saved["error"], { kind: "domain-failed", message: "Save failed.", failure: { $case: "titleRequired" } });
  assert.equal("protocol" in saved, false, "only snapshot replies state the protocol");
  assert.deepEqual(await client.commandOutcome("editorSave", decodeFailure), bridgeFailure({ $case: "titleRequired" }));

  const operation = await client.startOperation("Save", "save-1", () => "save-1", value => value, false, decodeFailure);
  assert.deepEqual(await operation.outcome(), bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  const stream = await client.startOperation("Publish", "publish-1", () => "publish-1", value => bridgeWire.string(value), true, decodeFailure);
  const terminal = await stream.wait();
  assert.equal(terminal.kind === "domain-failed" && terminal.stream, true);
  const page = await stream.stream();
  assert.deepEqual({ kind: page.kind, completed: page.completed, items: page.items }, { kind: "domain-failed", completed: true, items: [{ sequence: 1, value: "one" }] });
  const held = await client.startOperation("Hold", "hold-1", () => "hold-1", value => value, false, decodeFailure);
  editor.operations("Hold")[0]!.failWith({ $case: "titleRequired" });
  assert.deepEqual(await held.outcome(), bridgeFailure({ $case: "titleRequired" }));

  bridge.failNext("editorRename", { kind: "domain-failed", failure: { $case: "titleRequired" } });
  assert.deepEqual(await client.commandOutcome("editorRename", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
  bridge.failNext("editorStartRename", { kind: "domain-failed", failure: { $case: "titleRequired" } });
  const injected = await client.startOperation("Rename", "rename-1", () => "rename-1", value => value, false, decodeFailure);
  assert.deepEqual(await injected.outcome(), bridgeFailure({ $case: "titleRequired" }));
  client.dispose();
});

// What a generated mockEditor() passes, with failure encoders.
interface EditorClient {
  save(): Promise<BridgeOutcome<EditorState, SaveFailure>>;
  rename(): Promise<EditorState>;
  startSave(): Promise<BridgeOperation<void, SaveFailure>>;
}
type EditorMock = MockTypedView<EditorState, EditorClient> & {
  readonly operations: { readonly save: readonly MockTypedOperation<void, void, SaveFailure>[] };
};
const encodeFailure = (value: unknown) => {
  const failure = value as SaveFailure;
  return failure.$case === "titleTaken" ? { $case: "titleTaken", existingTitle: failure.existingTitle.toUpperCase() } : failure;
};

test("a typed mock encodes declared failures", async () => {
  const bridge = fresh();
  const editor = mockTypedView(bridge, {
    kind: "editor", route: "editor", contract,
    fields: { title: { encode: value => value, decode: wire => bridgeWire.string(wire) } },
    defaults: {},
    commands: { save: { route: "Save", encodeFailure }, rename: { route: "Rename" } },
    operations: { save: { member: "Save", encodeFailure } },
  }, {
    state: { title: "" },
    commands: { save: () => { throw mockFailure<SaveFailure>({ $case: "titleTaken", existingTitle: "todo" }); } },
    operations: { save: "manual" },
  }) as EditorMock;
  expectType<SaveFailure>()(undefined as unknown as MockFailureOf<EditorClient["save"]>);
  expectType<SaveFailure>()(undefined as unknown as MockFailureOf<EditorClient["startSave"]>);
  expectType<never>()(undefined as unknown as MockFailureOf<EditorClient["rename"]>);
  // @ts-expect-error rename declares no failure
  editor.failNext("rename", { kind: "domain-failed", failure: { $case: "titleRequired" } });

  const client = await connect();
  const taken = bridgeFailure({ $case: "titleTaken", existingTitle: "TODO" });
  assert.deepEqual(await client.commandOutcome("editorSave", decodeFailure), taken);
  editor.failNext("save", { kind: "domain-failed", failure: { $case: "titleTaken", existingTitle: "next" } });
  assert.deepEqual(await client.commandOutcome("editorSave", decodeFailure), bridgeFailure({ $case: "titleTaken", existingTitle: "NEXT" }));

  const operation = await client.startOperation("Save", "save-1", () => "save-1", value => value, false, decodeFailure);
  editor.operations.save[0]!.failWith({ $case: "titleTaken", existingTitle: "op" });
  assert.deepEqual(await operation.outcome(), bridgeFailure({ $case: "titleTaken", existingTitle: "OP" }));
  editor.failNext("startSave", { kind: "domain-failed", failure: { $case: "titleRequired" } });
  const injected = await client.startOperation("Save", "save-2", () => "save-2", value => value, false, decodeFailure);
  assert.deepEqual(await injected.outcome(), bridgeFailure({ $case: "titleRequired" }));
  client.dispose();
});

// specs/application/fixtures/domain-failures: the wire .NET writes and the
// outcome a declared client reports.
interface Fixture {
  readonly scenario: string;
  readonly wire: Record<string, unknown>;
  readonly client: { readonly outcome?: { readonly ok: false; readonly failure: unknown }; readonly error?: { readonly kind: string; readonly message: string } };
}
const fixtures = new URL("../../../../specs/application/fixtures/domain-failures/", import.meta.url);

test("the portable domain failure fixtures decode as specified", async () => {
  const files = readdirSync(fixtures).filter(name => name.endsWith(".json")).sort();
  assert.deepEqual(files, ["command-reply.json", "operation-status.json", "retention-dropped-status.json", "stream-status.json",
    "undeclared-command-reply.json"]);
  for (const file of files) {
    const fixture = JSON.parse(readFileSync(new URL(file, fixtures), "utf8")) as Fixture;
    let settled: Promise<unknown>;
    let dispose = () => {};
    if (fixture.scenario.endsWith("command-reply")) {
      const bridge = fresh();
      bridge.view("editor", { state: { title: "" } });
      // "state": {} stands for the model's state.
      bridge.route("editorSubmit", () => JSON.stringify({ ...fixture.wire, state: { revision: 2, title: "" } }));
      const client = await connect();
      dispose = () => client.dispose();
      settled = client.commandOutcome("editorSubmit", decodeFailure);
    } else {
      const { operation, client } = await operationWith(fixture.wire, { stream: fixture.wire["stream"] === true });
      dispose = () => client.dispose();
      settled = operation.outcome();
    }
    if (fixture.client.outcome) assert.deepEqual(await settled, bridgeFailure(fixture.client.outcome.failure), file);
    else await assert.rejects(settled, (error: unknown) => error instanceof BridgeError
      && error.kind === fixture.client.error!.kind && error.message === fixture.client.error!.message, file);
    dispose();
  }
});
