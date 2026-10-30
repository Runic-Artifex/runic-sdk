// presentation-epoch/1 and presented/1 on the client (specs/application/README.md, "Presentation epochs",
// "Presented states and window close"): the epoch a scoped command carries, superseded commands, `present` and
// `closing`.
import assert from "node:assert/strict";
import { test } from "node:test";
import { createCommandController, isBridgeOutcome, onBridgeConnection, onBridgeDiagnostic, type BridgeConnectionState, type RunicBridgeClient } from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type State = { readonly page: number; readonly title: string };
const members = { page: (value: unknown) => bridgeWire.integer(value, 0, 1000), title: bridgeWire.string };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));
const allFeatures = ["text-splice/1", "presentation-epoch/1", "presented/1"];

interface Sent { readonly message: Record<string, unknown>; resolve(reply: object): void }

/**
 * A host bridge whose producer lists `features`. `open` answers at once with revision 1, epoch 1 and page 1 (the epoch
 * only when the feature is listed); `command`, `presented` and `closing-reply` wait for the test to answer them.
 */
function installBridge(features: readonly string[] = allFeatures) {
  const listeners = new Set<(message: string) => void>();
  const connection = new Set<(connected: boolean) => void>();
  let connected = true;
  let revision = 1;
  const sent: Sent[] = [];
  const epoch = (value: number) => features.includes("presentation-epoch/1") ? { epoch: value } : {};
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    onConnectionChange(report) { connection.add(report); report(connected); return () => { connection.delete(report); }; },
    onMessage(next) { listeners.add(next); return () => { listeners.delete(next); }; },
    request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      if (!connected) return Promise.reject(new Error("disconnected"));
      switch (message["kind"]) {
        case "hello": return Promise.resolve(JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features }));
        case "open": return Promise.resolve(JSON.stringify({ ok: true, presentation: "ok", revision, ...epoch(1), state: { page: 1, title: "start" } }));
        case "close": return Promise.resolve(JSON.stringify({ ok: true, presentation: "ok" }));
        default: return new Promise(resolve => { sent.push({ message, resolve: reply => resolve(JSON.stringify(reply)) }); });
      }
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  const push = (message: object) => { for (const listener of [...listeners]) listener(JSON.stringify(message)); };
  return {
    sent,
    get revision() { return revision; },
    /** Takes the next request the page sent, which must be of `kind`. */
    async take(kind: string): Promise<Sent> {
      for (let waited = 0; sent.length === 0 && waited < 100; waited++) await tick(1);
      const next = sent.shift();
      assert.ok(next, `the page sent no ${kind}`);
      assert.equal(next.message["kind"], kind, JSON.stringify(next.message));
      return next;
    },
    /** A renewal: a full state at the next revision with a new epoch, which is that revision. */
    state(state: State, epochValue = revision + 1) {
      revision++;
      push({ kind: "state", route: "wizard", revision, epoch: epochValue, state });
    },
    frame(changes: object[]) {
      push({ kind: "frame", route: "wizard", baseRevision: revision, revision: revision + 1, changes });
      revision++;
    },
    push,
    setConnected(next: boolean) {
      connected = next;
      for (const report of [...connection]) report(next);
    },
  };
}

type FrameHost = {
  requestAnimationFrame?: ((callback: () => void) => number) | undefined;
  cancelAnimationFrame?: ((handle: number) => void) | undefined;
  document?: { visibilityState: string };
};

/** Animation frames that run only when the test says so. */
function installFrames() {
  const frameHost = globalThis as FrameHost;
  const saved = { request: frameHost.requestAnimationFrame, cancel: frameHost.cancelAnimationFrame, document: frameHost.document };
  let callbacks = new Map<number, () => void>();
  let next = 0;
  frameHost.requestAnimationFrame = callback => { callbacks.set(++next, callback); return next; };
  frameHost.cancelAnimationFrame = handle => { callbacks.delete(handle); };
  frameHost.document = { visibilityState: "visible" };
  return {
    get requested() { return callbacks.size; },
    run() { const due = callbacks; callbacks = new Map(); for (const callback of due.values()) callback(); },
    restore() {
      frameHost.requestAnimationFrame = saved.request;
      frameHost.cancelAnimationFrame = saved.cancel;
      if (saved.document === undefined) delete frameHost.document; else frameHost.document = saved.document;
    },
  };
}

const connectWizard = () => connectView<State>({ contract: "Tests.Wizard:fp-1", route: "wizard", members, scopedCommands: ["next", "back"] });

test("hello lists presentation-epoch/1 and presented/1", async () => {
  const requests: Record<string, unknown>[] = [];
  const bridge = installBridge();
  const original = (host["__runicBridge"] as RunicBridgeClient).request;
  (host["__runicBridge"] as { request: RunicBridgeClient["request"] }).request = text => { requests.push(JSON.parse(text)); return original(text); };
  const client = await connectWizard();
  assert.deepEqual(requests.find(message => message["kind"] === "hello")?.["features"], allFeatures);
  client.dispose();
  void bridge;
});

test("a scoped command carries the epoch of the state its presentation last received; an unscoped one carries none", async () => {
  const frames = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectWizard();
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.page));
    void client.command("next");
    assert.equal((await bridge.take("command")).message["epoch"], 1);
    void client.command("ping");
    assert.equal(Object.hasOwn((await bridge.take("command")).message, "epoch"), false);

    // A renewal that changed no member: the presentation takes its epoch without a notification.
    bridge.state({ page: 1, title: "start" }, 2);
    assert.equal(frames.requested, 0);
    void client.command("next");
    assert.equal((await bridge.take("command")).message["epoch"], 2);
    assert.deepEqual(seen, [1]);

    // A renewal with a new page is applied at once and delivered with the next animation frame: a click before
    // that frame was made on the page still on screen, so it carries the epoch that page was delivered with.
    bridge.state({ page: 2, title: "start" }, 3);
    assert.equal(client.snapshot.page, 1);
    void client.command("back");
    assert.equal((await bridge.take("command")).message["epoch"], 2);
    frames.run();
    assert.deepEqual(seen, [1, 2]);
    void client.command("back");
    assert.equal((await bridge.take("command")).message["epoch"], 3);
    // Frames never change the epoch.
    bridge.frame([{ field: "title", kind: "set", value: "typed" }]);
    frames.run();
    void client.command("next");
    assert.equal((await bridge.take("command")).message["epoch"], 3);
    client.dispose();
  } finally { frames.restore(); }
});

test("a superseded command resolves like one that ran, after its revision, without a diagnostic or a controller error", async () => {
  const bridge = installBridge();
  const diagnostics: string[] = [];
  const stop = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic.code); });
  try {
    const client = await connectWizard();
    const command = client.command("next");
    const sent = await bridge.take("command");
    // The producer renewed the route meanwhile: the reply names a revision the page has not reached yet.
    sent.resolve({ ok: true, revision: 2, superseded: true });
    let resolved = false;
    void command.then(() => { resolved = true; });
    await tick(5);
    assert.equal(resolved, false);
    bridge.state({ page: 2, title: "start" }, 2);
    assert.deepEqual(await command, { page: 2, title: "start" });

    const outcome = client.commandOutcome("back", value => value);
    (await bridge.take("command")).resolve({ ok: true, revision: 2, superseded: true });
    const result = await outcome;
    assert.ok(isBridgeOutcome(result) && result.ok);
    assert.deepEqual(result.ok ? result.value : undefined, { page: 2, title: "start" });

    const controller = createCommandController(() => client.command("next"));
    const run = controller.run();
    (await bridge.take("command")).resolve({ ok: true, revision: 2, superseded: true });
    assert.deepEqual(await run, { page: 2, title: "start" });
    assert.deepEqual(controller.current, { pending: false, error: undefined, failure: undefined });
    controller[Symbol.dispose]();
    assert.deepEqual(diagnostics, []);
    client.dispose();
  } finally { stop(); }
});

test("without presentation-epoch/1 a scoped command carries no epoch, and an epoch in a state is ignored", async () => {
  const bridge = installBridge(["text-splice/1", "presented/1"]);
  const client = await connectWizard();
  bridge.state({ page: 2, title: "start" }, 9);
  void client.command("next");
  assert.equal(Object.hasOwn((await bridge.take("command")).message, "epoch"), false);
  client.dispose();
});

test("present: the page reports presented after the revision, its delivery and one animation frame", async () => {
  const frames = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectWizard();
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.page));
    bridge.push({ kind: "present", route: "wizard", revision: 2 });
    await tick(5);
    assert.equal(bridge.sent.length, 0);
    // The route reaches revision 2: delivered at once, then reported after one frame.
    bridge.frame([{ field: "page", kind: "set", value: 2 }]);
    await tick(5);
    assert.deepEqual(seen, [1, 2]);
    assert.equal(bridge.sent.length, 0);
    frames.run();
    const presented = await bridge.take("presented");
    assert.deepEqual(presented.message, { kind: "presented", route: "wizard", revision: 2 });
    // A failed report is ignored.
    presented.resolve({ ok: false, error: { kind: "rejected", message: "no", reason: "invalid-request" } });
    await tick(5);
    assert.equal(bridge.sent.length, 0);
    client.dispose();
  } finally { frames.restore(); }
});

test("present: a route without a subscriber reports once one subscribes", async () => {
  const bridge = installBridge();
  const client = await connectWizard();
  bridge.push({ kind: "present", route: "wizard", revision: 1 });
  await tick(5);
  assert.equal(bridge.sent.length, 0);
  const stop = client.subscribe(() => {});
  assert.deepEqual((await bridge.take("presented")).message, { kind: "presented", route: "wizard", revision: 1 });
  stop();
  client.dispose();
});

test("present: one report runs per route, and it reports the highest revision asked for", async () => {
  const bridge = installBridge();
  const client = await connectWizard();
  client.subscribe(() => {});
  bridge.push({ kind: "present", route: "wizard", revision: 2 });
  bridge.push({ kind: "present", route: "wizard", revision: 3 });
  bridge.frame([{ field: "page", kind: "set", value: 2 }]);
  bridge.frame([{ field: "page", kind: "set", value: 3 }]);
  const first = await bridge.take("presented");
  assert.equal(first.message["revision"], 3);
  // While a report is on its way, later asks wait for it and coalesce.
  bridge.push({ kind: "present", route: "wizard", revision: 3 });
  bridge.push({ kind: "present", route: "wizard", revision: 1 });
  await tick(5);
  assert.equal(bridge.sent.length, 0);
  first.resolve({ ok: true });
  const second = await bridge.take("presented");
  assert.equal(second.message["revision"], 3);
  second.resolve({ ok: true });
  await tick(5);
  assert.equal(bridge.sent.length, 0);
  client.dispose();
});

test("without presented/1 a present push is ignored", async () => {
  const bridge = installBridge(["text-splice/1", "presentation-epoch/1"]);
  const client = await connectWizard();
  client.subscribe(() => {});
  bridge.push({ kind: "present", route: "wizard", revision: 1 });
  bridge.push({ kind: "closing" });
  await tick(5);
  assert.equal(bridge.sent.length, 0);
  client.dispose();
});

test("closing: the page delivers its state, waits a frame, replies, and reports closed until a new connection", async () => {
  const frames = installFrames();
  const states: BridgeConnectionState[] = [];
  let stop = () => {};
  try {
    const bridge = installBridge();
    const client = await connectWizard();
    stop = onBridgeConnection(state => states.push(state), { lostAfter: 0 });
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.page));
    bridge.frame([{ field: "page", kind: "set", value: 2 }]);
    bridge.push({ kind: "closing" });
    // The pending notification is delivered first; the reply waits for a frame.
    assert.deepEqual(seen, [1, 2]);
    await tick(5);
    assert.equal(bridge.sent.length, 0);
    frames.run();
    const reply = await bridge.take("closing-reply");
    assert.deepEqual(reply.message, { kind: "closing-reply" });
    assert.deepEqual(states, ["connected", "closed"]);
    reply.resolve({ ok: true });
    // The host ends the connection: still closed, not lost.
    bridge.setConnected(false);
    await tick(5);
    assert.deepEqual(states, ["connected", "closed"]);
    // A subscription that starts now reports closed too.
    const late: BridgeConnectionState[] = [];
    const stopLate = onBridgeConnection(state => late.push(state));
    assert.deepEqual(late, ["closed"]);
    bridge.setConnected(true);
    await tick(5);
    assert.deepEqual(states, ["connected", "closed", "connected"]);
    assert.deepEqual(late, ["closed", "connected"]);
    stopLate();
    client.dispose();
  } finally {
    stop();
    frames.restore();
  }
});

test("with the mock Bridge: a double click on a scoped command runs once, waitPresented and closeWindow complete", async () => {
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  const view = bridge.view("wizard", {
    state: { page: 1, title: "start" },
    commands: { next: state => { view.renew(); return { page: (state["page"] as number) + 1 }; } },
  });
  const diagnostics: string[] = [];
  const stopDiagnostics = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic.code); });
  const states: BridgeConnectionState[] = [];
  const stopConnection = onBridgeConnection(state => states.push(state));
  try {
    const client = await connectWizard();
    client.subscribe(() => {});
    const [first, second] = await Promise.all([client.command("next"), client.command("next")]);
    assert.deepEqual([first.page, second.page, view.state["page"]], [2, 2, 2]);
    const commands = bridge.calls.filter(call => call.kind === "command").map(call => call.message["epoch"]);
    assert.equal(commands.length, 2);
    assert.equal(commands[0], commands[1]);
    assert.equal((await client.command("next")).page, 3);
    assert.equal(await view.waitPresented(1_000), true);
    assert.equal(await bridge.closeWindow(1_000), true);
    assert.equal(states.at(-1), "closed");
    bridge.disconnect();
    assert.equal(states.at(-1), "closed");
    assert.deepEqual(diagnostics, []);
    client.dispose();
  } finally {
    stopDiagnostics();
    stopConnection();
    (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
    delete host[runtimeKey];
  }
});
