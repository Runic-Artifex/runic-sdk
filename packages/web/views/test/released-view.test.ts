// Issue #246: a page's own command can replace or leave the page, so .NET releases
// the page's View during the call. Replies for such a View resolve when the call
// ran, or reject as `released`; none is reported as `failed`. Issue #406: `released`
// is not `disconnected`, which means the Bridge itself is not connected, and a
// released View is expected after navigation, so it is not reported as a diagnostic.
import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, bridgeSuccess, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { bridgeOperations, connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

type PageState = { readonly title: string };
const contract = "Tests.Page:fixture";
const hydrate = (wire: Record<string, unknown>): PageState => ({ title: bridgeWire.string(wire["title"]) });

function fresh(): MockBridge {
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  return installMockBridge(createMockBridge());
}

const connect = () => connectView({ contract, route: "page", mount: false, hydrate, operations: bridgeOperations });

// The replies .NET sends for a call that released its own View (BridgeRuntime.EncodeReleased).
const released = JSON.stringify({ ok: true, state: null, error: null, released: true });
const releasedRejected = JSON.stringify({ ok: false, state: null, error: { kind: "rejected", message: "Back is unavailable." }, released: true });
// What Runic Desktop and CS-WebUI answer for a route they released (specs/application/fixtures/released/unbound-route.json).
const unbound = JSON.stringify({ ok: false, state: null, error: { kind: "released", message: "This View was released." } });
const routes = ["pageSnapshot", "pageBack", "pageSetTitle", "pageWriteTitle", "pageCanBack", "pageStartSave"];

function captureDiagnostics() {
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic); });
  return { diagnostics, stop };
}

const isKind = (kind: string, route: string) => (error: unknown) =>
  error instanceof BridgeError && error.kind === kind && error.route === route;

test("a call that released its own View resolves with the last state and reports nothing", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  bridge.route("pageBack", () => released);
  bridge.route("pageClose", () => released);
  const client = await connect();
  const { diagnostics, stop } = captureDiagnostics();
  try {
    assert.deepEqual(await client.command("pageBack"), { title: "detail" });
    assert.deepEqual(await client.commandOutcome("pageClose", () => { throw new Error("no failure is declared"); }), bridgeSuccess({ title: "detail" }));
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
    client.dispose();
  }
});

test("a failed call that released its View keeps its kind", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  bridge.route("pageBack", () => releasedRejected);
  const client = await connect();
  await assert.rejects(client.command("pageBack"), isKind("rejected", "pageBack"));
  client.dispose();
});

test("a successful reply without state that does not say the View was released is still invalid", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  bridge.route("pageBack", () => JSON.stringify({ ok: true, state: null, error: null }));
  const client = await connect();
  await assert.rejects(client.command("pageBack"),
    (error: unknown) => isKind("failed", "pageBack")(error) && (error as BridgeError).message === "The Bridge returned no state.");
  client.dispose();
});

test("a released route is released on every route and reports nothing", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  for (const route of routes) bridge.route(route, () => unbound);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(client.command("pageBack"), isKind("released", "pageBack"));
    await assert.rejects(client.invoke("pageSetTitle", "x"), isKind("released", "pageSetTitle"));
    await assert.rejects(client.query("pageCanBack"), isKind("released", "pageCanBack"));
    await assert.rejects(client.startOperation("Save", "request-1", () => "{}", value => value),
      isKind("released", "pageStartSave"));
    // A checked write's reply is an envelope with a receipt.
    await assert.rejects(client.writeField("pageWriteTitle", "{}", value => value), isKind("released", "pageWriteTitle"));
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
    client.dispose();
  }
});

test("a View connecting to a released route is released", async () => {
  const bridge = fresh();
  bridge.route("pageSnapshot", () => unbound);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(connect(), isKind("released", "pageSnapshot"));
    assert.deepEqual(diagnostics, []);
  } finally {
    stop();
  }
});

// Runic Desktop also answers an empty reply when a handler threw, the capability was never
// admitted or the call could not be decoded. That is a failure, not a released View.
test("an empty reply is an invalid response on every route", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  for (const route of routes) bridge.route(route, () => "");
  const invalid = (route: string) => (error: unknown) => isKind("failed", route)(error) && /invalid/.test((error as BridgeError).message);
  await assert.rejects(client.command("pageBack"), invalid("pageBack"));
  await assert.rejects(client.invoke("pageSetTitle", "x"), invalid("pageSetTitle"));
  await assert.rejects(client.query("pageCanBack"), invalid("pageCanBack"));
  await assert.rejects(client.startOperation("Save", "request-1", () => "{}", value => value), invalid("pageStartSave"));
  await assert.rejects(client.writeField("pageWriteTitle", "{}", value => value), invalid("pageWriteTitle"));
  client.dispose();
  fresh().route("pageSnapshot", () => "");
  await assert.rejects(connect(), invalid("pageSnapshot"));
});

test("an operation start on a released View is released", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  bridge.route("pageStartSave", () => JSON.stringify({ kind: "released", reason: "The Bridge is closed." }));
  const client = await connect();
  await assert.rejects(client.startOperation("Save", "request-1", () => "{}", value => value), isKind("released", "pageStartSave"));
  client.dispose();
});

// The page navigated: its framework disposed the client while a call was in flight.
test("a reply for a View the page released during the call is released and reports nothing", async () => {
  const bridge = installMockBridge(createMockBridge({ scheduling: "manual" }));
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  bridge.view("page", { state: { title: "detail" } });
  const client = await bridge.flushUntil(connect());
  const { diagnostics, stop } = captureDiagnostics();
  try {
    const pending = client.invoke("pageSetTitle", "x");
    void pending.catch(() => undefined);
    client.dispose();
    await bridge.flush();
    await assert.rejects(pending, isKind("released", "pageSetTitle"));
    await assert.rejects(client.command("pageBack"), (error: unknown) => error instanceof BridgeError && error.kind === "released");
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
    await assert.rejects(client.command("pageBack"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
    client.dispose();
    await assert.rejects(client.command("pageBack"), (error: unknown) => error instanceof BridgeError && error.kind === "disconnected");
    assert.deepEqual(diagnostics.map(item => [item.kind, item.code]), [["error", "disconnected"], ["error", "disconnected"]]);
  } finally {
    stop();
  }
});
