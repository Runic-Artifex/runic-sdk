// Issue #246: a page's own command can replace or leave the page, so the producer releases the page's route during
// the call. Replies for such a call carry `released: true`: they resolve when the call succeeded, or keep their error;
// none is reported as `failed`. Issue #406: `released` is not `disconnected`, which means the Bridge itself is not
// connected, and a released View is expected after navigation, so it is not reported as a diagnostic.
import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, BridgeOperationUncertainError, bridgeSuccess, onBridgeDiagnostic, type BridgeDiagnostic, type RunicBridgeClient } from "../dist/index.js";
import { bridgeOperations, connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

type PageState = { readonly title: string };
const contract = "Tests.Page:fixture";
const members = { title: bridgeWire.string };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");

function fresh(bridge: MockBridge = createMockBridge()): MockBridge {
  const host = globalThis as unknown as Record<symbol, { dispose?: () => void } | undefined>;
  host[runtimeKey]?.dispose?.();
  delete host[runtimeKey];
  return installMockBridge(bridge);
}

const connect = () => connectView<PageState>({ contract, route: "page", members, checkedFields: ["title"], operations: bridgeOperations });
const codec = { decode: (value: unknown) => value };
const writeRequest = { requestId: "write-1", expectedVersion: 0, expectedValue: "detail", value: "x" };

function captureDiagnostics() {
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic); });
  return { diagnostics, stop };
}

const isKind = (kind: string, route = "page") => (error: unknown) =>
  error instanceof BridgeError && error.kind === kind && error.route === route;

test("a call that released its own View resolves with the last state and reports nothing", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" }, checkedFields: ["title"], commands: { back: () => undefined, close: () => undefined } });
  const client = await connect();
  bridge.intercept(call => call.kind === "command" || call.kind === "set" ? { ok: true, released: true } : undefined);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    assert.deepEqual(await client.command("back"), { title: "detail" });
    assert.deepEqual(await client.commandOutcome("close", () => { throw new Error("no failure is declared"); }), bridgeSuccess({ title: "detail" }));
    // The edit's overlay ends with the reply: the producer released the route before the value arrived.
    assert.deepEqual(await client.set("title", "x"), { title: "detail" });
    assert.deepEqual(client.snapshot, { title: "detail" });
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
    client.dispose();
  }
});

test("a failed call that released its View keeps its kind", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  bridge.intercept(call => call.kind === "command"
    ? { ok: false, released: true, error: { kind: "rejected", message: "Back is unavailable.", reason: "unavailable" } } : undefined);
  await assert.rejects(client.command("back"), (error: unknown) => isKind("rejected")(error) && (error as BridgeError).reason === "unavailable");
  client.dispose();
});

test("a successful reply without a revision that does not say the View was released is invalid", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  bridge.intercept(call => call.kind === "command" ? { ok: true } : undefined);
  await assert.rejects(client.command("back"),
    (error: unknown) => isKind("failed")(error) && (error as BridgeError).message === "The Bridge returned an invalid response.");
  client.dispose();
});

test("a released route is released on every request kind and reports nothing", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" }, checkedFields: ["title"] });
  const client = await connect();
  // What a producer answers for a route it does not serve (specs/application, "Errors").
  bridge.intercept(call => call.route === "page" ? { ok: false, error: { kind: "released", message: "This View was released." } } : undefined);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(client.command("back"), isKind("released"));
    await assert.rejects(client.commandOutcome("back", value => value), isKind("released"));
    await assert.rejects(client.set("title", "x"), isKind("released"));
    await assert.rejects(client.can("back"), isKind("released"));
    await assert.rejects(client.write("title", writeRequest, bridgeWire.string), isKind("released"));
    await assert.rejects(client.start("save", "request-1", undefined, codec), isKind("released"));
    await assert.rejects(client.recover("save", "request-1", codec), isKind("released"));
    assert.deepEqual(client.snapshot, { title: "detail" }, "the failed edit's overlay ended");
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
    client.dispose();
  }
});

test("a View connecting to a released route is released and reports nothing", async () => {
  fresh();
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(connect(), isKind("released"));
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
  }
});

// A reply that is not a JSON object of the profile, including an empty string, is an invalid response.
test("an empty reply is an invalid response on every request kind", async () => {
  const mock = createMockBridge();
  let empty = false;
  const bridge: RunicBridgeClient = {
    isConnected: () => mock.isConnected(),
    request: text => empty && (JSON.parse(text) as { route?: string }).route === "page" ? Promise.resolve("") : mock.request(text),
    onMessage: listener => mock.onMessage(listener),
    onConnectionChange: listener => mock.onConnectionChange(listener),
  };
  fresh(mock);
  (globalThis as { __runicBridge?: RunicBridgeClient }).__runicBridge = bridge;
  mock.view("page", { state: { title: "detail" }, checkedFields: ["title"] });
  const client = await connect();
  empty = true;
  const invalid = (error: unknown) => isKind("failed")(error) && /invalid response/.test((error as BridgeError).message);
  await assert.rejects(client.command("back"), invalid);
  await assert.rejects(client.set("title", "x"), invalid);
  await assert.rejects(client.can("back"), invalid);
  await assert.rejects(client.write("title", writeRequest, bridgeWire.string), invalid);
  // A start whose reply cannot be read may have been admitted, and its status cannot be read either.
  await assert.rejects(client.start("save", "request-1", undefined, codec),
    (error: unknown) => error instanceof BridgeOperationUncertainError && error.requestId === "request-1" && error.route === "page");
  // The close is answered normally; an invalid close reply would be reported.
  empty = false;
  client.dispose();
  await new Promise(resolve => setTimeout(resolve, 0));
  empty = true;
  await assert.rejects(connect(), invalid);
});

// The page navigated: its framework disposed the client while a call was in flight. The route is no longer open,
// so the call resolves at once when its reply arrives (specs/application, "Client resolution"); later calls are
// released.
test("a call in flight when the page released its View resolves, later calls are released, and nothing is reported", async () => {
  const bridge = fresh(createMockBridge({ scheduling: "manual" }));
  bridge.view("page", { state: { title: "detail" }, commands: { back: () => undefined } });
  const client = await bridge.flushUntil(connect());
  const { diagnostics, stop } = captureDiagnostics();
  try {
    const pending = client.set("title", "x");
    void pending.catch(() => undefined);
    client.dispose();
    await bridge.flush();
    assert.deepEqual(await pending, { title: "x" });
    assert.deepEqual(bridge.calls.map(call => call.kind).slice(-2), ["set", "close"]);
    await assert.rejects(client.command("back"), isKind("released"));
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
  }
});

// A host that ended rejects calls; that is `disconnected` and stays a reported error.
test("a call while the Bridge is disconnected is disconnected and reported", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  const { diagnostics, stop } = captureDiagnostics();
  try {
    bridge.disconnect();
    await assert.rejects(client.command("back"), isKind("disconnected"));
    client.dispose();
    await assert.rejects(client.command("back"), isKind("disconnected"));
    assert.deepEqual(diagnostics.map(item => [item.kind, item.code, item.route]), [["error", "disconnected", "page"], ["error", "disconnected", "page"]]);
  } finally {
    stop();
  }
});
