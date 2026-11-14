// trace-context/1 and handler-timing/1 (specs/application/README.md, "Trace context and handler timing"): while a reader of
// the development Bridge-call channel listens, a call's request carries the trace context the reader gave it and asks
// for its C# handler's timing, but only on a connection that uses the feature; the end event carries the handler a
// reply named. Without a reader, nothing is added, and a producer of an earlier release sees requests as before.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import type { BridgeCallEvent, BridgeCallTrace, RunicBridgeClient } from "../dist/index.js";
import { bridgeOperations, connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";

const callsKey = Symbol.for("runic.views.calls");
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Demo.EditorViewModel:ABC";
const traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
const baseFeatures = ["request-session/1", "presentation-epoch/1"];
type State = { readonly title: string };
const members = { title: bridgeWire.string };
type End = Extract<BridgeCallEvent, { phase: "end" }>;

function reset(): void {
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  delete host[callsKey];
  delete host["__runicBridge"];
}

afterEach(reset);

/**
 * A host bridge whose producer lists `features` and answers every call at once. A request that asks for timing is
 * answered with `handler` when `handler` is set, as a .NET host in Development does.
 */
function installBridge(features: readonly string[], handler?: { name: string; duration: number }) {
  const requests: Record<string, unknown>[] = [];
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    onConnectionChange(report) { report(true); return () => {}; },
    onMessage() { return () => {}; },
    async request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      requests.push(message);
      const kind = message["kind"];
      const timed = message["timing"] === true && handler !== undefined ? { handler } : {};
      if (kind === "hello") return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features });
      if (kind === "open") return JSON.stringify({ ok: true, presentation: "ok", revision: 1, state: { title: "a" } });
      if (kind === "can") return JSON.stringify({ ok: true, value: true, ...timed });
      return JSON.stringify({ ok: true, revision: 1, ...timed });
    },
  };
  host["__runicBridge"] = bridge;
  return { requests, calls: () => requests.filter(request => !["hello", "open", "close", "presented"].includes(request["kind"] as string)) };
}

function listen(trace?: () => BridgeCallTrace | undefined) {
  const events: BridgeCallEvent[] = [];
  host[callsKey] = {
    listeners: new Set([(event: BridgeCallEvent) => { events.push(event); }]), requested: true, devtools: false,
    ...trace === undefined ? {} : { trace },
  };
  return { events, ends: () => events.filter(event => event.phase === "end") as End[] };
}

const connect = () => connectView<State>({ contract, route: "editor", members, operations: bridgeOperations, checkedFields: ["title"] });

test("a followed call carries its trace context and asks for timing; the end event names the C# handler", async () => {
  const { calls } = installBridge([...baseFeatures, "trace-context/1", "handler-timing/1"], { name: "EditorViewModel.Save", duration: 3.5 });
  const reader = listen(() => ({ traceparent, tracestate: "runic=1" }));
  const client = await connect();
  await client.command("save");
  const [request] = calls();
  assert.deepEqual(request!["trace"], { traceparent, tracestate: "runic=1" });
  assert.equal(request!["timing"], true);
  const [start, end] = reader.events;
  assert.equal(start!.phase, "start");
  assert.deepEqual(start!.trace, { traceparent, tracestate: "runic=1" });
  const ended = end as End;
  assert.equal(ended.phase, "end");
  assert.deepEqual(ended.trace, start!.trace);
  assert.equal(ended.handler?.name, "EditorViewModel.Save");
  assert.equal(ended.handler?.duration, 3.5);
  // On the page's clock, starting inside the call, 3.5 ms long.
  assert.ok(ended.handler!.start >= ended.start && ended.handler!.start <= ended.end, JSON.stringify(ended));
  assert.ok(Math.abs(ended.handler!.end - ended.handler!.start - 3.5) < 1e-9);
  client.dispose();
});

test("set, can, write and operation starts carry the members too", async () => {
  const { calls } = installBridge([...baseFeatures, "trace-context/1", "handler-timing/1"], { name: "EditorViewModel.Title", duration: 0.25 });
  const reader = listen(() => ({ traceparent }));
  const client = await connect();
  await client.set("title", "b");
  await client.can("save");
  // The fake host's receipt is not a valid one; only the request matters here.
  await client.write("title", { requestId: "w1", expectedVersion: 1, expectedValue: "a", value: "c" } as never, bridgeWire.string).catch(() => undefined);
  await client.start("export", "op-1", undefined, { result: () => undefined } as never).catch(() => undefined);
  // The fake host's start reply is not a valid admission either, so the page recovers with a status after it.
  const sent = calls().slice(0, 4);
  assert.deepEqual(sent.map(request => request["kind"]), ["set", "can", "write", "start"]);
  for (const request of sent) {
    assert.deepEqual(request["trace"], { traceparent }, JSON.stringify(request));
    assert.equal(request["timing"], true, JSON.stringify(request));
  }
  assert.equal(reader.ends().find(event => event.kind === "set")?.handler?.name, "EditorViewModel.Title");
  client.dispose();
});

test("an older producer gets no new members, and one without handler-timing/1 (production) names no handler", async () => {
  const older = installBridge(baseFeatures, { name: "ignored", duration: 1 });
  const reader = listen(() => ({ traceparent }));
  let client = await connect();
  await client.command("save");
  assert.equal(older.calls()[0]!["trace"], undefined);
  assert.equal(older.calls()[0]!["timing"], undefined);
  assert.equal(reader.ends()[0]!.handler, undefined);
  // The trace context is still the call's, for the reader's own span.
  assert.deepEqual(reader.ends()[0]!.trace, { traceparent });
  client.dispose();
  reset();

  const production = installBridge([...baseFeatures, "trace-context/1"]);
  const second = listen(() => ({ traceparent }));
  client = await connect();
  await client.command("save");
  assert.deepEqual(production.calls()[0]!["trace"], { traceparent });
  assert.equal(production.calls()[0]!["timing"], undefined);
  assert.equal(second.ends()[0]!.handler, undefined);
  client.dispose();
});

test("without a reader nothing is added, and a reader without a trace hook gets only the handler's timing", async () => {
  const bridge = installBridge([...baseFeatures, "trace-context/1", "handler-timing/1"], { name: "EditorViewModel.Save", duration: 1 });
  let client = await connect();
  await client.command("save");
  assert.deepEqual(Object.keys(bridge.calls()[0]!).sort(), ["kind", "member", "route", "session"]);
  client.dispose();

  // The Vite development client without an OTLP export.
  const reader = listen();
  client = await connect();
  await client.command("save");
  const request = bridge.calls()[1]!;
  assert.equal(request["trace"], undefined);
  assert.equal(request["timing"], true);
  assert.equal(reader.ends()[0]!.trace, undefined);
  assert.equal(reader.ends()[0]!.handler?.name, "EditorViewModel.Save");
  client.dispose();
});

test("a trace hook that throws or returns nothing leaves the call untraced", async () => {
  const bridge = installBridge([...baseFeatures, "trace-context/1"]);
  const errors: unknown[] = [];
  const saved = console.error;
  console.error = (error: unknown) => { errors.push(error); };
  try {
    const reader = listen(() => { throw new Error("no randomness"); });
    const client = await connect();
    await client.command("save");
    (host[callsKey] as { trace: () => undefined }).trace = () => undefined;
    await client.command("save");
    assert.equal(errors.length, 1);
    assert.ok(bridge.calls().every(request => request["trace"] === undefined));
    assert.equal(reader.ends().length, 2);
    client.dispose();
  } finally {
    console.error = saved;
  }
});
