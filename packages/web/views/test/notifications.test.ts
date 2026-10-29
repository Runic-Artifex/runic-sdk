import assert from "node:assert/strict";
import { test } from "node:test";
import { waitForBridge, type RunicBridgeClient } from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";

type State = { readonly count: number; readonly title: string };
const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1000), title: bridgeWire.string };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

/** A host bridge that delivers each push at once, as a WebSocket message is its own task, and answers commands. */
function installBridge() {
  let listener: ((message: string) => void) | undefined;
  const connection = new Set<(connected: boolean) => void>();
  let connected = true;
  let revision = 0;
  // A command's reply: its revision, after the frame that published it.
  const commandReplies: ((value: string) => void)[] = [];
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    onConnectionChange(report) { connection.add(report); report(connected); return () => { connection.delete(report); }; },
    onMessage(next) { listener = next; return () => { listener = undefined; }; },
    request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      switch (message["kind"]) {
        case "hello": return Promise.resolve(JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] }));
        case "open": return Promise.resolve(JSON.stringify({ ok: true, presentation: "ok", revision, state: { count: 0, title: "start" } }));
        case "command": return new Promise(resolve => { commandReplies.push(resolve); });
        case "set": return Promise.resolve(JSON.stringify({ ok: true, revision }));
        default: return Promise.resolve(JSON.stringify({ ok: true }));
      }
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  const frame = (changes: object[]) => {
    listener!(JSON.stringify({ kind: "frame", route: "page", baseRevision: revision, revision: revision + 1, changes }));
    revision++;
  };
  return {
    frame,
    get revision() { return revision; },
    answerCommand() { commandReplies.shift()!(JSON.stringify({ ok: true, revision })); },
    setConnected(next: boolean) { connected = next; for (const report of [...connection]) report(next); },
  };
}

type FrameHost = {
  requestAnimationFrame?: ((callback: () => void) => number) | undefined;
  cancelAnimationFrame?: ((handle: number) => void) | undefined;
  document?: { visibilityState: string };
};

/** Installs animation frames that run only when the test says so, and a document with a visibility state. */
function installFrames(visibility = "visible") {
  const frameHost = globalThis as FrameHost;
  const saved = { request: frameHost.requestAnimationFrame, cancel: frameHost.cancelAnimationFrame, document: frameHost.document };
  let callbacks = new Map<number, () => void>();
  let next = 0;
  frameHost.requestAnimationFrame = callback => { callbacks.set(++next, callback); return next; };
  frameHost.cancelAnimationFrame = handle => { callbacks.delete(handle); };
  const documentHost = { visibilityState: visibility };
  frameHost.document = documentHost;
  return {
    get requested() { return callbacks.size; },
    hide() { documentHost.visibilityState = "hidden"; },
    run() { const due = callbacks; callbacks = new Map(); for (const callback of due.values()) callback(); },
    restore() {
      frameHost.requestAnimationFrame = saved.request;
      frameHost.cancelAnimationFrame = saved.cancel;
      if (saved.document === undefined) delete frameHost.document; else frameHost.document = saved.document;
    },
  };
}

async function connectPage() {
  return connectView<State>({ contract: "Tests.Page:fingerprint", route: "page", members });
}

test("frames apply at once and reach subscribers once per animation frame", async () => {
  const frames = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectPage();
    const seen: State[] = [];
    const other: number[] = [];
    client.subscribe(state => seen.push(state));
    client.subscribe(state => other.push(state.count));
    for (let count = 1; count <= 5; count++) bridge.frame([{ field: "count", kind: "set", value: count }]);
    // Applied, but not delivered yet: the snapshot is what subscribers last received.
    assert.deepEqual(seen.map(state => state.count), [0]);
    assert.equal(client.snapshot.count, 0);
    assert.equal(frames.requested, 1);
    frames.run();
    assert.deepEqual(seen.map(state => state.count), [0, 5]);
    assert.deepEqual(other, [0, 5]);
    assert.equal(client.snapshot, seen.at(-1));
    // A frame that changes nothing the page sees schedules nothing.
    bridge.frame([{ field: "count", kind: "set", value: 5 }]);
    assert.equal(frames.requested, 0);
    client.dispose();
  } finally { frames.restore(); }
});

test("a call resolves with the state of every frame that arrived before its reply, without waiting for a frame", async () => {
  const frames = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectPage();
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.count));
    const command = client.command("increment");
    await tick();
    bridge.frame([{ field: "count", kind: "set", value: 1 }]);
    bridge.frame([{ field: "title", kind: "set", value: "later" }]);
    assert.deepEqual(seen, [0]);
    bridge.answerCommand();
    const state = await command;
    assert.deepEqual(state, { count: 1, title: "later" });
    assert.deepEqual(seen, [0, 1]);
    assert.equal(client.snapshot, state);
    // The edit of a setter shows at once, as for a controlled input.
    const set = client.set("title", "typed");
    assert.equal(client.snapshot.title, "typed");
    assert.equal(seen.length, 3);
    await set;
    frames.run();
    client.dispose();
  } finally { frames.restore(); }
});

test("a hidden page receives frames through a timer, and so does a frame that the page hides before", async () => {
  const frames = installFrames("hidden");
  try {
    const bridge = installBridge();
    const client = await connectPage();
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.count));
    bridge.frame([{ field: "count", kind: "set", value: 1 }]);
    bridge.frame([{ field: "count", kind: "set", value: 2 }]);
    assert.equal(frames.requested, 0);
    assert.deepEqual(seen, [0]);
    for (let waited = 0; seen.length < 2 && waited < 1000; waited += 5) await tick(5);
    assert.deepEqual(seen, [0, 2]);
    client.dispose();
  } finally { frames.restore(); }

  const visible = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectPage();
    const seen: number[] = [];
    client.subscribe(state => seen.push(state.count));
    bridge.frame([{ field: "count", kind: "set", value: 3 }]);
    assert.equal(visible.requested, 1);
    // The page is hidden before its animation frame: the browser does not run it.
    visible.hide();
    for (let waited = 0; seen.length < 2 && waited < 1000; waited += 10) await tick(10);
    assert.deepEqual(seen, [0, 3]);
    assert.equal(visible.requested, 0);
    client.dispose();
  } finally { visible.restore(); }
});

test("a page connects when its Bridge reports the connection, without waiting for the next check", async () => {
  const bridge = installBridge();
  bridge.setConnected(false);
  const connecting = waitForBridge({ timeout: 2000 });
  await tick(60);
  const reported = Date.now();
  bridge.setConnected(true);
  await connecting;
  assert.ok(Date.now() - reported < 40, `connected ${Date.now() - reported} ms after the report`);
});
