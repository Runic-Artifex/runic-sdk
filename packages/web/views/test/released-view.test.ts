// Issue #246: a page's own command can replace or leave the page, so .NET releases
// the page's View during the call. Replies for such a View resolve when the call
// ran, or reject as `disconnected`; none is reported as `failed`.
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
// What Runic Desktop and CS-WebUI answer for a route they released.
const unbound = JSON.stringify({ ok: false, state: null, error: { kind: "disconnected", message: "This view is no longer connected." } });
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

test("a released route is disconnected on every route", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  const client = await connect();
  for (const route of routes) bridge.route(route, () => unbound);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(client.command("pageBack"), isKind("disconnected", "pageBack"));
    await assert.rejects(client.invoke("pageSetTitle", "x"), isKind("disconnected", "pageSetTitle"));
    await assert.rejects(client.query("pageCanBack"), isKind("disconnected", "pageCanBack"));
    await assert.rejects(client.startOperation("Save", "request-1", () => "{}", value => value),
      isKind("disconnected", "pageStartSave"));
    // A checked write's reply is an envelope with a receipt.
    await assert.rejects(client.writeField("pageWriteTitle", "{}", value => value), isKind("disconnected", "pageWriteTitle"));
    assert.deepEqual(diagnostics.map(item => [item.route, item.code]), ["pageBack", "pageSetTitle", "pageCanBack", "pageStartSave", "pageWriteTitle"]
      .map(route => [route, "disconnected"]));
  } finally {
    stop();
    client.dispose();
  }
});

test("a View connecting to a released route is disconnected", async () => {
  const bridge = fresh();
  bridge.route("pageSnapshot", () => unbound);
  const { diagnostics, stop } = captureDiagnostics();
  try {
    await assert.rejects(connect(), isKind("disconnected", "pageSnapshot"));
    assert.deepEqual(diagnostics.map(item => item.code), ["disconnected"]);
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

test("an operation start on a released View is disconnected", async () => {
  const bridge = fresh();
  bridge.view("page", { state: { title: "detail" } });
  bridge.route("pageStartSave", () => JSON.stringify({ kind: "disconnected", reason: "The Bridge is closed." }));
  const client = await connect();
  await assert.rejects(client.startOperation("Save", "request-1", () => "{}", value => value), isKind("disconnected", "pageStartSave"));
  client.dispose();
});
