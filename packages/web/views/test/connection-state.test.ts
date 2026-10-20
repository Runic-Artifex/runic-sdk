// Issue #406: a page observes its connection to the .NET host as it changes,
// without making a call, and tells a dead host from a released View.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { onBridgeConnection, type BridgeConnectionState, type RunicBridgeClient } from "../dist/index.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type Host = typeof globalThis & { __runicBridge?: RunicBridgeClient };
const host = globalThis as Host;
const delay = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));

afterEach(() => { delete host.__runicBridge; });

// A host script's Bridge: `onConnectionChange` reports the current state, then each change.
function hostBridge(initial: boolean) {
  let connected = initial;
  const listeners = new Set<(connected: boolean) => void>();
  let isConnectedCalls = 0;
  const bridge: RunicBridgeClient = {
    isConnected: () => { isConnectedCalls++; return connected; },
    call: () => Promise.reject(new Error("not used")),
    onConnectionChange(listener) {
      listeners.add(listener);
      listener(connected);
      return () => { listeners.delete(listener); };
    },
  };
  return {
    bridge,
    get listeners() { return listeners.size; },
    get isConnectedCalls() { return isConnectedCalls; },
    set(value: boolean) {
      connected = value;
      for (const listener of [...listeners]) listener(value);
    },
  };
}

function observe(options?: { lostAfter?: number }) {
  const states: BridgeConnectionState[] = [];
  const stop = onBridgeConnection(state => { states.push(state); }, options);
  return { states, stop };
}

test("a dropped connection is reconnecting at once and connected again after the reconnect", () => {
  const fake = hostBridge(true);
  host.__runicBridge = fake.bridge;
  const { states, stop } = observe();
  assert.deepEqual(states, ["connected"]);
  fake.set(false);
  assert.deepEqual(states, ["connected", "reconnecting"]);
  fake.set(true);
  assert.deepEqual(states, ["connected", "reconnecting", "connected"]);
  stop();
  fake.set(false);
  assert.deepEqual(states, ["connected", "reconnecting", "connected"]);
  assert.equal(fake.listeners, 0, "Stopping did not unsubscribe from the host.");
  // The runtime never polls a host's isConnected(): CS-WebUI's allows navigation.
  assert.equal(fake.isConnectedCalls, 0);
});

test("a connection that stays down for lostAfter is lost, and lost is not terminal", async () => {
  const fake = hostBridge(true);
  host.__runicBridge = fake.bridge;
  const { states, stop } = observe({ lostAfter: 20 });
  fake.set(false);
  await delay(60);
  assert.deepEqual(states, ["connected", "reconnecting", "lost"]);
  fake.set(true);
  assert.deepEqual(states, ["connected", "reconnecting", "lost", "connected"]);
  // A reconnect within lostAfter cancels the timer.
  fake.set(false);
  fake.set(true);
  await delay(60);
  assert.deepEqual(states, ["connected", "reconnecting", "lost", "connected", "reconnecting", "connected"]);
  stop();
});

test("lostAfter 0 is lost as soon as the connection drops", () => {
  const fake = hostBridge(true);
  host.__runicBridge = fake.bridge;
  const { states, stop } = observe({ lostAfter: 0 });
  fake.set(false);
  assert.deepEqual(states, ["connected", "lost"]);
  stop();
});

test("lostAfter must be a non-negative finite number", () => {
  for (const lostAfter of [-1, Number.NaN, Number.POSITIVE_INFINITY])
    assert.throws(() => onBridgeConnection(() => {}, { lostAfter }), RangeError);
});

test("a Bridge that has not connected yet is connecting, then connected", async () => {
  const fake = hostBridge(false);
  host.__runicBridge = fake.bridge;
  const { states, stop } = observe({ lostAfter: 1_000 });
  assert.deepEqual(states, ["connecting"]);
  fake.set(true);
  assert.deepEqual(states, ["connecting", "connected"]);
  stop();
});

test("without an installed Bridge the page is connecting, lost after lostAfter, and connects when a host installs one", async () => {
  const { states, stop } = observe({ lostAfter: 20 });
  assert.deepEqual(states, ["connecting"]);
  await delay(60);
  assert.deepEqual(states, ["connecting", "lost"]);
  host.__runicBridge = hostBridge(true).bridge;
  await delay(120);
  assert.deepEqual(states, ["connecting", "lost", "connected"]);
  stop();
});

// A host script that predates onConnectionChange: the first connection and each reconnect are seen, a loss is not.
test("an older host reports its first connection and reconnects", async () => {
  let connected = false;
  const reconnects = new Set<() => void>();
  host.__runicBridge = {
    isConnected: () => connected,
    call: () => Promise.reject(new Error("not used")),
    onReconnect(listener) { reconnects.add(listener); return () => { reconnects.delete(listener); }; },
  };
  const { states, stop } = observe({ lostAfter: 1_000 });
  assert.deepEqual(states, ["connecting"]);
  connected = true;
  await delay(120);
  assert.deepEqual(states, ["connecting", "connected"]);
  for (const listener of reconnects) listener();
  assert.deepEqual(states, ["connecting", "connected"]);
  stop();
  assert.equal(reconnects.size, 0);
});

test("an unloading page does not report the socket closing", () => {
  const fake = hostBridge(true);
  host.__runicBridge = fake.bridge;
  const { states, stop } = observe();
  globalThis.dispatchEvent(new Event("pagehide"));
  fake.set(false);
  assert.deepEqual(states, ["connected"]);
  // Restored from the back/forward cache: the current state is reported again.
  globalThis.dispatchEvent(new Event("pageshow"));
  assert.deepEqual(states, ["connected", "reconnecting"]);
  stop();
});

test("a throwing listener does not stop the subscription", () => {
  const fake = hostBridge(true);
  host.__runicBridge = fake.bridge;
  const states: BridgeConnectionState[] = [];
  const report = (globalThis as { reportError?: unknown }).reportError;
  (globalThis as { reportError?: unknown }).reportError = () => {};
  try {
    const stop = onBridgeConnection(state => { states.push(state); throw new Error("listener"); });
    fake.set(false);
    assert.deepEqual(states, ["connected", "reconnecting"]);
    stop();
  } finally {
    (globalThis as { reportError?: unknown }).reportError = report;
  }
});

test("the mock Bridge reports disconnect() and reconnect()", () => {
  const bridge = installMockBridge(createMockBridge());
  const { states, stop } = observe();
  bridge.disconnect();
  bridge.reconnect();
  assert.deepEqual(states, ["connected", "reconnecting", "connected"]);
  stop();
});

// D40 a: the first View that connects publishes the page's connection on a registered channel,
// which the Vite plugin's development client forwards to its dock.
test("connecting a View publishes the connection on the shared channel", async () => {
  const { connectView } = await import("../dist/generated.js");
  const key = Symbol.for("runic.views.connection");
  const page = globalThis as unknown as Record<symbol, { listeners: Set<(state: string) => void>; state?: string } | undefined>;
  const seen: string[] = [];
  page[key] = { listeners: new Set([state => seen.push(state)]) };
  const fake = hostBridge(true);
  host.__runicBridge = { ...fake.bridge, call: async () => JSON.stringify({ ok: true, state: { revision: 1 }, error: null }) };
  const client = await connectView({ contract: "Tests.Shared:1", route: "shared", mount: false, hydrate: (wire: unknown) => wire });
  assert.equal(page[key]!.state, "connected");
  fake.set(false);
  assert.equal(page[key]!.state, "reconnecting");
  assert.deepEqual(seen, ["connected", "reconnecting"]);
  client.dispose();
  fake.set(true);
  delete page[key];
});
