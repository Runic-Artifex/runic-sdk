// W130-029: declared failures resolve a BridgeOutcome; unexpected failures
// still reject with BridgeError. Generated clients do not call these APIs
// until the generator emits failure codecs, so the tests call them directly.
// The handshake's protocol and version checks are here too, because they decide how every call fails.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import {
  BridgeError, BridgeOperationUncertainError, bridgeFailure, bridgeSuccess, createCommandController, isBridgeOutcome, matchCase,
  onBridgeDiagnostic, type BridgeDiagnostic, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome, type CommandState,
  type RunicBridgeClient,
} from "../dist/index.js";
import { bridgeOperations, connectView, type BridgeOperationCodec } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import {
  createMockBridge, installMockBridge, mockFailure, type MockBridge, type MockFailureOf, type MockTypedOperation,
  type MockTypedView,
} from "../dist/mock.js";
import { mockTypedView } from "../dist/generated-mock.js";
import { viewsVersion } from "../dist/version.js";

type SaveFailure = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
type EditorState = { readonly title: string };
const contract = "Tests.Editor:fixture";
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");

const decodeFailure = (wire: unknown): SaveFailure => bridgeWire.object(wire, value => {
  switch (value["$case"]) {
    case "titleRequired": return { $case: "titleRequired" };
    case "titleTaken": return { $case: "titleTaken", existingTitle: bridgeWire.string(value["existingTitle"]) };
    default: throw new TypeError("Unknown SaveFailure case.");
  }
});
const members = { title: bridgeWire.string };

function fresh(): MockBridge {
  const host = globalThis as unknown as Record<symbol, { dispose?: () => void } | undefined>;
  host[runtimeKey]?.dispose?.();
  delete host[runtimeKey];
  return installMockBridge(createMockBridge());
}

const connect = (route = "editor") => connectView<EditorState>({ contract, route, members, operations: bridgeOperations });

function captureReports() {
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  const reported: unknown[] = [];
  target.reportError = error => { reported.push(error); };
  return { reported, restore: () => { target.reportError = previous; } };
}

function captureDiagnostics() {
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic); });
  return { diagnostics, stop };
}

const until = async (condition: () => boolean) => {
  for (let waited = 0; !condition() && waited < 2_000; waited += 10) await new Promise(resolve => setTimeout(resolve, 10));
};

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
  assert.equal((success as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.BridgeOutcome")], true);
  assert.equal((success as unknown as Record<symbol, unknown>)[Symbol.for("runic.bridgeOutcome")], undefined, "The pre-1.0 key is gone.");
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
    commands: {
      save: () => { throw mockFailure(failure); },
      rename: () => ({ title: "renamed" }),
      broken: () => { throw new Error("disk full"); },
    },
  });
  const client = await connect();
  const outcome = await client.commandOutcome("save", decodeFailure);
  expectType<BridgeOutcome<EditorState, SaveFailure>>()(outcome);
  assert.equal(isBridgeOutcome(outcome), true);
  assert.deepEqual(outcome, bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  const renamed = await client.commandOutcome("rename", decodeFailure);
  assert.equal(renamed.ok && renamed.value.title, "renamed");
  assert.equal(client.snapshot.title, "renamed");
  await assert.rejects(client.commandOutcome("broken", decodeFailure),
    (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.message === "disk full" && error.route === "editor");
  failure = { $case: "unknown" };
  await assert.rejects(client.commandOutcome("save", decodeFailure),
    (error: unknown) => error instanceof BridgeError && error.kind === "failed" && /invalid failure/.test(error.message));
  client.dispose();
});

// specs/application, "Revisions in replies": a declared failure whose effects cannot be published has the reason
// `state-withheld` and no revision. The outcome resolves, and the withheld state is reported.
test("a declared failure with a withheld state resolves and reports the withheld state", async () => {
  const bridge = fresh();
  const editor = bridge.view("editor", { state: { title: "" } });
  bridge.intercept(call => {
    if (call.member === "save") return { ok: false, error: { kind: "domain-failed", reason: "state-withheld",
      message: "Save failed. The updated state could not be sent: rows has a duplicate key.", failure: { $case: "titleRequired" } } };
    if (call.member === "plain") return { ok: false, revision: editor.revision, error: { kind: "domain-failed", message: "Save failed.",
      failure: { $case: "titleRequired" } } };
    return undefined;
  });
  const client = await connect();
  const reports = captureReports();
  try {
    assert.deepEqual(await client.commandOutcome("save", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
    assert.equal(reports.reported.length, 1);
    const reported = reports.reported[0];
    assert.ok(reported instanceof BridgeError && reported.kind === "failed" && reported.route === "editor"
      && reported.reason === "state-withheld" && /could not be sent/.test(reported.message));
    assert.deepEqual(await client.commandOutcome("plain", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
    assert.equal(reports.reported.length, 1, "a declared failure with its revision is not a key failure");
  } finally {
    reports.restore();
    client.dispose();
  }
});

// D02, specs/application "Handshake": the hello reply names the producer, and a page whose @runic-artifex/views
// release differs from the Runic.Application.Views it talks to learns it from one diagnostic. Other producers are
// not compared.
test("a hello from another Runic.Application.Views release is reported once, and other producers are not compared", async () => {
  // The mock with another producer's name and version in its hello replies.
  const producer = (name: string, version: string) => (bridge: MockBridge) => {
    (globalThis as { __runicBridge?: RunicBridgeClient }).__runicBridge = {
      isConnected: () => bridge.isConnected(),
      async request(text) {
        const reply = await bridge.request(text);
        return (JSON.parse(text) as { kind: string }).kind !== "hello" ? reply
          : JSON.stringify({ ...JSON.parse(reply) as object, implementation: { name, version } });
      },
      onMessage: listener => bridge.onMessage(listener),
      onConnectionChange: listener => bridge.onConnectionChange(listener),
    };
  };
  const { diagnostics, stop } = captureDiagnostics();
  try {
    for (const install of [producer("Runic.Application.Views", viewsVersion), producer("Tests.OtherProducer", "0.1.0-other")]) {
      const quiet = fresh();
      install(quiet);
      quiet.view("editor", { state: { title: "" } });
      (await connect()).dispose();
    }
    assert.deepEqual(diagnostics.filter(diagnostic => diagnostic.code === "version"), []);

    const bridge = fresh();
    producer("Runic.Application.Views", "0.1.0-other")(bridge);
    bridge.view("editor", { state: { title: "" } });
    bridge.view("other", { state: { title: "" } });
    const first = await connect();
    const second = await connect("other");
    // A new connection says hello again, with the same producer.
    bridge.disconnect();
    bridge.reconnect();
    await until(() => bridge.calls.filter(call => call.kind === "open").length >= 4);
    const reported = diagnostics.filter(diagnostic => diagnostic.code === "version");
    assert.equal(reported.length, 1);
    assert.equal(reported[0]!.kind, "error");
    assert.match(reported[0]!.message, new RegExp(`Runic.Application.Views 0.1.0-other, but this page uses @runic-artifex/views ${viewsVersion.replaceAll(".", "\\.")}`));
    assert.equal(first.snapshot.title, "", "the client keeps working");
    assert.equal((await first.set("title", "x")).title, "x");
    first.dispose();
    second.dispose();
  } finally {
    stop();
  }
});

test("the package version matches its manifest", async () => {
  const manifest = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
  assert.equal(viewsVersion, manifest.version);
});

test("an unknown or undeclared error kind is a failed BridgeError", async () => {
  const bridge = fresh();
  const editor = bridge.view("editor", { state: { title: "" } });
  bridge.intercept(call => call.kind !== "command" ? undefined : { ok: false, revision: editor.revision,
    error: { kind: call.member === "save" ? "domain-failed" : call.member === "old" ? "disconnected" : "throttled", message: "Save failed.", failure: {} } });
  const client = await connect();
  for (const member of ["save", "future", "old"])
    await assert.rejects(client.command(member), (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.message === "Save failed.");
  client.dispose();
});

// specs/application "Handshake": a client accepts only a producer of its own protocol version. A mixed pair, such
// as a protocol-3 (0.10) .NET host and this page, fails every call with one `protocol` diagnostic and sends no open.
test("a hello reply of another protocol version fails the connect with one protocol diagnostic", async () => {
  const mismatch = (route: string, host: RegExp) => (error: unknown) => error instanceof BridgeError && error.kind === "failed"
    && error.route === route && error.reason === "protocol-mismatch" && host.test(error.message)
    && /this client implements protocol 4/.test(error.message)
    && /Runic\.Application\.Views and @runic-artifex\/views of one release/.test(error.message);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    const bridge = fresh();
    bridge.intercept(call => call.kind === "hello"
      ? { ok: false, protocol: 3, implementation: { name: "Runic.Application.Views", version: "0.10.0" },
        error: { kind: "failed", reason: "protocol-mismatch", message: "The producer implements protocol 3." } } : undefined);
    bridge.view("editor", { state: { title: "" } });
    bridge.view("other", { state: { title: "" } });
    await assert.rejects(connect(), mismatch("editor", /uses Views protocol 3/));
    await assert.rejects(connect("other"), mismatch("other", /uses Views protocol 3/));
    // One cause in the console: no generic `failed` and no `version` diagnostic beside it.
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.kind, diagnostic.code]), [["error", "protocol"]]);
    assert.match(diagnostics[0]!.message, /uses Views protocol 3, but this client implements protocol 4/);
    // The first open went out with the hello (README "Handshake"); nothing is sent after the refusal.
    assert.equal(bridge.calls.filter(call => call.kind === "open").length, 1);

    // A successful hello reply without `protocol` is refused too.
    const unstated = fresh();
    unstated.intercept(call => call.kind === "hello" ? { ok: true } : undefined);
    unstated.view("editor", { state: { title: "" } });
    diagnostics.length = 0;
    await assert.rejects(connect(), mismatch("editor", /does not state a Views protocol/));
    assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), ["protocol"]);

    const quiet = fresh();
    quiet.view("editor", { state: { title: "" } });
    diagnostics.length = 0;
    const current = await connect();
    assert.equal(diagnostics.some(diagnostic => diagnostic.code === "protocol"), false);
    current.dispose();
  } finally {
    stop();
  }
});

// specs/application "Host bridge": a host script of protocol 3 or earlier installs `call(name)` instead of `request`.
test("a host bridge without request is refused as protocol 3 with one protocol diagnostic", async () => {
  const host = globalThis as unknown as Record<string | symbol, unknown>;
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const calls: string[] = [];
  const legacy = {
    isConnected: () => true,
    call: (name: string) => { calls.push(name); return Promise.resolve(JSON.stringify({ ok: true, state: { revision: 0 }, error: null, protocol: 3 })); },
    onConnectionChange: (listener: (connected: boolean) => void) => { listener(true); return () => {}; },
  };
  host["__runicBridge"] = legacy as unknown as RunicBridgeClient;
  const { diagnostics, stop } = captureDiagnostics();
  try {
    const refused = (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.reason === "protocol-mismatch"
      && /implements Views protocol 3 or earlier/.test(error.message) && /this client implements protocol 4/.test(error.message);
    await assert.rejects(connect(), refused);
    await assert.rejects(connect("other"), refused);
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.kind, diagnostic.code]), [["error", "protocol"]]);
    assert.deepEqual(calls, [], "the client sends nothing to a host of another protocol");
  } finally {
    stop();
    delete host["__runicBridge"];
  }
});

test("a reopen after a reconnect fails when the host now speaks another protocol", async () => {
  const bridge = fresh();
  bridge.view("editor", { state: { title: "" } });
  const client = await connect();
  const { diagnostics, stop } = captureDiagnostics();
  const reports = captureReports();
  try {
    // A dev host rebuilt against packages of another release.
    bridge.intercept(call => call.kind === "hello"
      ? { ok: false, protocol: 5, implementation: { name: "Runic.Application.Views", version: "9.0.0" },
        error: { kind: "failed", reason: "protocol-mismatch", message: "The producer implements protocol 5." } } : undefined);
    bridge.view("editor", { state: { title: "stale" } });
    bridge.disconnect();
    bridge.reconnect();
    await until(() => reports.reported.length > 0);
    await bridge.flush();
    assert.equal(client.snapshot.title, "", "no state of a host of another protocol is accepted");
    assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), ["protocol"]);
    assert.equal(reports.reported.length, 1);
    assert.ok(reports.reported[0] instanceof BridgeError && reports.reported[0].kind === "failed" && reports.reported[0].reason === "protocol-mismatch");
    // The reopen went out with the new connection's hello; nothing is sent after the refusal.
    assert.equal(bridge.calls.filter(call => call.kind === "open").length, 2, "no open is sent after the refusal");
    await assert.rejects(client.command("save"), (error: unknown) => error instanceof BridgeError && error.reason === "protocol-mismatch");
    assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), ["protocol"]);
  } finally {
    reports.restore();
    stop();
    client.dispose();
  }
});

test("a rejected reply carries its reason, and an unknown reason is kept", async () => {
  const bridge = fresh();
  bridge.view("editor", { state: { title: "" } });
  const reasons: Record<string, unknown> = {
    save: "unavailable", setTitle: "invalid-input", delete: "unresolved-key", future: "rate-limited", plain: undefined, malformed: 7,
  };
  bridge.intercept(call => call.kind !== "command" ? undefined : { ok: false,
    error: { kind: "rejected", message: "Save was rejected.", ...(reasons[call.member!] === undefined ? {} : { reason: reasons[call.member!] }) } });
  const client = await connect();
  const reasonOf = async (member: string) => {
    try { await client.command(member); } catch (error) {
      assert.ok(error instanceof BridgeError && error.kind === "rejected" && error.route === "editor", String(error));
      return error.reason;
    }
    throw new Error(`${member} did not reject.`);
  };
  assert.equal(await reasonOf("save"), "unavailable");
  assert.equal(await reasonOf("setTitle"), "invalid-input");
  assert.equal(await reasonOf("delete"), "unresolved-key");
  assert.equal(await reasonOf("future"), "rate-limited");
  assert.equal(await reasonOf("plain"), undefined);
  // A reason that is not a string makes the error invalid, so the reply is an invalid response.
  await assert.rejects(client.command("malformed"), (error: unknown) => error instanceof BridgeError && error.kind === "failed"
    && /invalid response/.test(error.message));
  assert.equal(new BridgeError("rejected", "x", { reason: "unavailable" }).reason, "unavailable");
  client.dispose();
});

// Starts an operation whose watch answers `status`; a terminal status names the route's current revision.
async function operationWith(status: Record<string, unknown>, options: { declared?: boolean; stream?: boolean; decode?: (value: unknown) => unknown } = {}) {
  const bridge = fresh();
  const editor = bridge.view("editor", { state: { title: "" } });
  const withRevision = () => status["kind"] === "running" || status["kind"] === "expired" || status["kind"] === "unknown"
    ? status : { revision: editor.revision, ...status };
  bridge.intercept(call => {
    switch (call.kind) {
      case "start": return { ok: true, revision: editor.revision, admission: "accepted", status: { kind: "running" } };
      case "watch": return { ok: true, status: withRevision() };
      case "cancel": return { ok: true, cancellation: "cancellation-requested" };
      default: return undefined;
    }
  });
  const client = await connect();
  const codec: BridgeOperationCodec<unknown, SaveFailure> = {
    decode: options.decode ?? ((value: unknown) => bridgeWire.string(value)), stream: options.stream ?? false,
    ...(options.declared === false ? {} : { decodeFailure }),
  };
  const operation = await client.start("save", "save-1", undefined, codec);
  return { bridge, client, operation };
}

test("outcome() follows the terminal status", async () => {
  const rejectsWith = (kind: string, message?: RegExp) => (error: unknown) =>
    error instanceof BridgeError && error.kind === kind && error.route === "editor" && (message === undefined || message.test(error.message));

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
  const reports = captureReports();
  try {
    const undecodable = await operationWith({ kind: "domain-failed", failure: { $case: "other" } });
    await assert.rejects(undecodable.operation.outcome(), rejectsWith("failed", /failure is invalid/));
    // The invalid status is also reported, for subscribers that have no promise to reject.
    assert.ok(reports.reported.length === 1 && rejectsWith("failed", /failure is invalid/)(reports.reported[0]));
  } finally { reports.restore(); }

  const failed = await operationWith({ kind: "failed", error: { kind: "failed", message: "The operation failed.",
    detail: { type: "System.InvalidOperationException", message: "disk full" } } });
  await assert.rejects(failed.operation.outcome(), (error: unknown) => rejectsWith("failed", /operation failed/)(error)
    && (error as BridgeError).detail?.message === "disk full");
  const cancelled = await operationWith({ kind: "cancelled" });
  await assert.rejects(cancelled.operation.outcome(), rejectsWith("cancelled"));
  for (const kind of ["expired", "unknown"]) {
    const uncertain = await operationWith({ kind });
    await assert.rejects(uncertain.operation.outcome(), (error: unknown) => error instanceof BridgeOperationUncertainError
      && error.requestId === "save-1" && error.contract === contract);
  }

  // A watch that never reaches a terminal status.
  const hanging = await operationWith({ kind: "running" });
  await assert.rejects(hanging.operation.outcome({ timeout: 0 }), (error: unknown) => rejectsWith("timeout")(error)
    && ((error as BridgeError).cause as { readonly cancellation?: string }).cancellation === "cancellation-requested");
  hanging.client.dispose();
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
      case "timed-out": return status.cancellation;
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
    commands: { save: () => { throw mockFailure({ $case: "titleRequired" }, "Save failed."); } },
    operations: {
      save: () => { throw mockFailure({ $case: "titleTaken", existingTitle: "Todo" }); },
      publish: async (_state, _input, operation) => { operation.emit("one"); throw mockFailure({ $case: "titleRequired" }); },
      hold: "manual",
    },
    streams: ["publish"],
  });
  const client = await connect();
  // The raw reply on the client's connection: the declared failure with the revision, and no detail.
  const saved = JSON.parse(await bridge.request(JSON.stringify({ kind: "command", route: "editor", member: "save" }))) as Record<string, unknown>;
  assert.deepEqual(saved, { ok: false, revision: editor.revision,
    error: { kind: "domain-failed", message: "Save failed.", failure: { $case: "titleRequired" } } });
  // The mock names itself and this release in every hello reply, as a producer does.
  const hello = JSON.parse(await bridge.request(JSON.stringify({ kind: "hello", protocol: 4, session: "s", client: { name: "c", version: "1" } }))) as Record<string, unknown>;
  assert.equal(hello["protocol"], 4);
  assert.deepEqual(hello["implementation"], { name: "@runic-artifex/views/mock", version: viewsVersion });
  assert.deepEqual(await client.commandOutcome("save", decodeFailure), bridgeFailure({ $case: "titleRequired" }));

  const operation = await client.start("save", "save-1", undefined, { decode: value => value, decodeFailure });
  assert.deepEqual(await operation.outcome(), bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  const stream = await client.start("publish", "publish-1", undefined, { decode: value => bridgeWire.string(value), decodeFailure, stream: true });
  const terminal = await stream.wait();
  assert.equal(terminal.kind === "domain-failed" && terminal.stream, true);
  const page = await stream.stream();
  assert.deepEqual({ kind: page.kind, completed: page.completed, items: page.items }, { kind: "domain-failed", completed: true, items: [{ sequence: 1, value: "one" }] });
  const held = await client.start("hold", "hold-1", undefined, { decode: value => value, decodeFailure });
  editor.operations("hold")[0]!.failWith({ $case: "titleRequired" });
  assert.deepEqual(await held.outcome(), bridgeFailure({ $case: "titleRequired" }));

  bridge.failNext({ route: "editor", kind: "command", member: "rename" }, { kind: "domain-failed", failure: { $case: "titleRequired" } });
  assert.deepEqual(await client.commandOutcome("rename", decodeFailure), bridgeFailure({ $case: "titleRequired" }));
  editor.failNext({ kind: "start", member: "rename" }, { kind: "domain-failed", failure: { $case: "titleRequired" } });
  const injected = await client.start("rename", "rename-1", undefined, { decode: value => value, decodeFailure });
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
    commands: { save: { member: "save", encodeFailure }, rename: { member: "rename" } },
    operations: { save: { member: "save", encodeFailure } },
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
  assert.deepEqual(await client.commandOutcome("save", decodeFailure), taken);
  editor.failNext("save", { kind: "domain-failed", failure: { $case: "titleTaken", existingTitle: "next" } });
  assert.deepEqual(await client.commandOutcome("save", decodeFailure), bridgeFailure({ $case: "titleTaken", existingTitle: "NEXT" }));

  const operation = await client.start("save", "save-1", undefined, { decode: value => value, decodeFailure });
  editor.operations.save[0]!.failWith({ $case: "titleTaken", existingTitle: "op" });
  assert.deepEqual(await operation.outcome(), bridgeFailure({ $case: "titleTaken", existingTitle: "OP" }));
  editor.failNext("startSave", { kind: "domain-failed", failure: { $case: "titleRequired" } });
  const injected = await client.start("save", "save-2", undefined, { decode: value => value, decodeFailure });
  assert.deepEqual(await injected.outcome(), bridgeFailure({ $case: "titleRequired" }));
  client.dispose();
});

// specs/application/fixtures/domain-failures: the wire .NET writes and the outcome a declared client reports. The
// fixtures predate protocol 4: a command reply's `"state": {}` stands for the state after the call, which a
// protocol-4 reply names by `revision`, and a terminal status gets the revision of the route.
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
      const editor = bridge.view("editor", { state: { title: "" } });
      const { state: _, ...reply } = fixture.wire;
      bridge.intercept(call => call.kind === "command" ? { ...reply, revision: editor.revision } : undefined);
      const client = await connect();
      dispose = () => client.dispose();
      settled = client.commandOutcome("submit", decodeFailure);
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
