// view-requests/1 on the client (specs/application/README.md, "View requests"): registration after `open`, generations,
// handler replacement and disposal, delivery once the page painted the request's revision, and errors.
import assert from "node:assert/strict";
import { test } from "node:test";
import type { RunicBridgeClient } from "../dist/index.js";
import { connectView, defineRequests } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";
import { mockTypedView, type MockTypedViewSpec } from "../dist/generated-mock.js";

type State = { readonly title: string };
const members = { title: bridgeWire.string };
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));
const allFeatures = ["text-splice/1", "presentation-epoch/1", "presented/1", "view-requests/1"];
const focusContract = "Tests.Notes:fp-1:request:Focus";
const toastContract = "Tests.Notes:fp-1:request:Toast";

const requests = defineRequests({
  focus: { contract: focusContract, decodeInput: value => bridgeWire.object(value, item => ({ member: bridgeWire.string(item["member"]) })) },
  toast: { contract: toastContract, decodeInput: value => bridgeWire.object(value, item => ({ text: bridgeWire.string(item["text"]) })) },
});
const connectNotes = () => connectView<State>({ contract: "Tests.Notes:fp-1", route: "notes", members, requests });
type Notes = Awaited<ReturnType<typeof connectNotes>>;
type Input<T> = (input: T, context: { readonly signal: AbortSignal }) => void | Promise<void>;
const handle = <T>(client: Notes, name: string, handler: Input<T>) => client.requests[name]!.handle(handler as Input<never>);

/**
 * A host bridge whose producer lists `features`. `open` answers at once with revision 1; `view-requests` answers
 * `registration` (default `ok`). Every request is recorded.
 */
function installBridge(features: readonly string[] = allFeatures) {
  const listeners = new Set<(message: string) => void>();
  const connection = new Set<(connected: boolean) => void>();
  let connected = true;
  let revision = 1;
  const sent: Record<string, unknown>[] = [];
  const state = { registration: "ok" };
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    onConnectionChange(report) { connection.add(report); report(connected); return () => { connection.delete(report); }; },
    onMessage(next) { listeners.add(next); return () => { listeners.delete(next); }; },
    request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      if (!connected) return Promise.reject(new Error("disconnected"));
      sent.push(message);
      const reply = (value: object) => Promise.resolve(JSON.stringify(value));
      switch (message["kind"]) {
        case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features });
        case "open": return reply({ ok: true, presentation: "ok", revision, state: { title: "start" } });
        case "close": return reply({ ok: true, presentation: "ok" });
        case "view-requests": return reply({ ok: true, registration: state.registration });
        case "command": return reply({ ok: true, revision });
        default: return reply({ ok: false, error: { kind: "rejected", reason: "invalid-request", message: "unexpected" } });
      }
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  const push = (message: object) => { for (const listener of [...listeners]) listener(JSON.stringify(message)); };
  return {
    sent,
    state,
    get revision() { return revision; },
    registrations: () => sent.filter(message => message["kind"] === "view-requests"),
    frame(title: string) {
      push({ kind: "frame", route: "notes", baseRevision: revision, revision: revision + 1, changes: [{ field: "title", kind: "set", value: title }] });
      revision++;
    },
    request(presentation: unknown, name: string, contract: string, input: unknown, at = revision) {
      push({ kind: "view-request", route: "notes", presentation, revision: at, name, contract, input });
    },
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

/** Captures what the runtime reports with `reportError`. */
function captureErrors() {
  const page = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const saved = page.reportError;
  const errors: unknown[] = [];
  page.reportError = error => { errors.push(error); };
  return { errors, restore() { page.reportError = saved; } };
}

const presentationOf = (bridge: ReturnType<typeof installBridge>) => bridge.sent.find(message => message["kind"] === "open")!["presentation"];

test("handlers register after open with increasing generations; replacing a handler resends nothing", async () => {
  const bridge = installBridge();
  const client = await connectNotes();
  assert.deepEqual(Object.keys(client.requests), ["focus", "toast"]);
  // Nothing is registered before the first handler.
  await tick();
  assert.deepEqual(bridge.registrations(), []);
  const presentation = presentationOf(bridge);

  const stopFocus = handle(client, "focus", () => {});
  await tick();
  assert.deepEqual(bridge.registrations(), [{
    kind: "view-requests", route: "notes", presentation, generation: 1, requests: [{ name: "focus", contract: focusContract }],
  }]);
  const stopToast = handle(client, "toast", () => {});
  await tick();
  assert.deepEqual(bridge.registrations().at(-1)!["requests"], [{ name: "focus", contract: focusContract }, { name: "toast", contract: toastContract }]);
  assert.equal(bridge.registrations().at(-1)!["generation"], 2);

  // A handler of the same request replaces it; the producer's registration does not change.
  const replacement = handle(client, "focus", () => {});
  await tick();
  assert.equal(bridge.registrations().length, 2);
  // The replaced handler's disposer does nothing.
  stopFocus();
  await tick();
  assert.equal(bridge.registrations().length, 2);
  replacement();
  await tick();
  assert.deepEqual(bridge.registrations().at(-1), {
    kind: "view-requests", route: "notes", presentation, generation: 3, requests: [{ name: "toast", contract: toastContract }],
  });
  stopToast();
  await tick();
  assert.deepEqual(bridge.registrations().at(-1)!["requests"], []);
  assert.equal(bridge.registrations().at(-1)!["generation"], 4);
  client.dispose();
  assert.throws(() => handle(client, "focus", () => {}), { kind: "released" });
});

test("a command waits for the handlers registered before it", async () => {
  const bridge = installBridge();
  const client = await connectNotes();
  handle(client, "focus", () => {});
  await client.command("touch");
  const kinds = bridge.sent.map(message => message["kind"]).filter(kind => kind === "view-requests" || kind === "command");
  assert.deepEqual(kinds, ["view-requests", "command"]);
  client.dispose();
});

test("a request runs once the route reached its revision, its state was delivered and one animation frame passed", async () => {
  const frames = installFrames();
  try {
    const bridge = installBridge();
    const client = await connectNotes();
    const seen: string[] = [];
    client.subscribe(state => seen.push(state.title));
    const inputs: unknown[] = [];
    let signal: AbortSignal | undefined;
    handle<{ member: string }>(client, "focus", (input, context) => {
      // The state of the request's revision is on screen.
      inputs.push({ ...input, title: client.snapshot.title, delivered: seen.at(-1) });
      signal = context.signal;
    });
    await tick();
    const presentation = presentationOf(bridge);
    // The request is raised at revision 2, which the page has not reached.
    bridge.request(presentation, "focus", focusContract, { member: "title" }, 2);
    await tick();
    assert.deepEqual(inputs, []);
    // The revision's state is delivered at once, as for a call that waited for it; the request waits for the next
    // animation frame.
    bridge.frame("raised");
    await tick();
    assert.deepEqual(seen, ["start", "raised"]);
    assert.deepEqual(inputs, []);
    assert.equal(frames.requested, 1);
    frames.run();
    await tick();
    assert.deepEqual(inputs, [{ member: "title", title: "raised", delivered: "raised" }]);
    assert.equal(signal?.aborted, false);

    // Requests run in the order they arrived, also when a later one names an earlier revision.
    const order: string[] = [];
    handle<{ text: string }>(client, "toast", input => { order.push(input.text); });
    await tick();
    bridge.request(presentation, "toast", toastContract, { text: "first" }, 3);
    bridge.request(presentation, "toast", toastContract, { text: "second" }, 2);
    bridge.frame("three");
    for (let index = 0; index < 4; index++) { frames.run(); await tick(); }
    assert.deepEqual(order, ["first", "second"]);
    client.dispose();
  } finally { frames.restore(); }
});

test("requests of another presentation, contract or name, without a handler, or after dispose are ignored", async () => {
  const errors = captureErrors();
  try {
    const bridge = installBridge();
    const client = await connectNotes();
    const inputs: unknown[] = [];
    handle(client, "focus", input => { inputs.push(input); });
    await tick();
    const presentation = presentationOf(bridge);
    bridge.request("other", "focus", focusContract, { member: "a" });
    bridge.request(presentation, "focus", "Tests.Notes:fp-2:request:Focus", { member: "b" });
    bridge.request(presentation, "unknown", focusContract, { member: "c" });
    bridge.request(presentation, "toast", toastContract, { text: "no handler" });
    await tick(5);
    assert.deepEqual(inputs, []);
    bridge.request(presentation, "focus", focusContract, { member: "d" });
    await tick(5);
    assert.deepEqual(inputs, [{ member: "d" }]);
    // A request that arrived before dispose and waits for its paint is not delivered after it.
    bridge.request(presentation, "focus", focusContract, { member: "e" }, bridge.revision + 1);
    client.dispose();
    bridge.frame("late");
    await tick(5);
    assert.deepEqual(inputs, [{ member: "d" }]);
    assert.deepEqual(errors.errors, []);
  } finally { errors.restore(); }
});

test("a handler's failure and an input that does not decode are reported; the handler is not called for the latter", async () => {
  const errors = captureErrors();
  try {
    const bridge = installBridge();
    const client = await connectNotes();
    const inputs: unknown[] = [];
    handle<{ member: string }>(client, "focus", input => {
      inputs.push(input);
      if (input.member === "throw") throw new Error("sync failure");
    });
    handle<{ text: string }>(client, "toast", async input => {
      inputs.push(input);
      throw new Error(`async failure ${input.text}`);
    });
    await tick();
    const presentation = presentationOf(bridge);
    bridge.request(presentation, "focus", focusContract, { member: 7 });
    bridge.request(presentation, "focus", focusContract, { member: "throw" });
    bridge.request(presentation, "toast", toastContract, { text: "x" });
    await tick(5);
    assert.deepEqual(inputs, [{ member: "throw" }, { text: "x" }]);
    assert.equal(errors.errors.length, 3);
    assert.match(String((errors.errors[0] as Error).message), /focus view request has an invalid input/);
    assert.equal((errors.errors[1] as Error).message, "sync failure");
    assert.equal((errors.errors[2] as Error).message, "async failure x");

    // An unsupported registration means the page and the host were generated from different contracts.
    bridge.state.registration = "unsupported";
    handle(client, "focus", () => {});
    const stop = handle(client, "focus", () => {});
    stop();
    await tick(5);
    assert.match(String((errors.errors.at(-1) as Error).message), /does not support the view requests/);
    client.dispose();
  } finally { errors.restore(); }
});

test("the signal aborts when the handler is replaced or unregistered, the View is disposed, or the connection is lost", async () => {
  const bridge = installBridge();
  const client = await connectNotes();
  const presentation = presentationOf(bridge);
  const signals: AbortSignal[] = [];
  const pending = (_input: unknown, context: { readonly signal: AbortSignal }) => {
    signals.push(context.signal);
    return new Promise<void>(resolve => { context.signal.addEventListener("abort", () => resolve()); });
  };
  const stop = handle(client, "focus", pending);
  await tick();
  bridge.request(presentation, "focus", focusContract, { member: "a" });
  await tick(5);
  handle(client, "focus", pending);
  assert.equal(signals[0]!.aborted, true);
  bridge.request(presentation, "focus", focusContract, { member: "b" });
  await tick(5);
  stop();
  // The first disposer no longer unregisters the replacement.
  assert.equal(signals[1]!.aborted, false);
  const stopToast = handle(client, "toast", pending);
  bridge.request(presentation, "toast", toastContract, { text: "c" });
  await tick(5);
  stopToast();
  assert.equal(signals[2]!.aborted, true);
  bridge.setConnected(false);
  assert.equal(signals[1]!.aborted, true);
  bridge.setConnected(true);
  await tick(5);
  bridge.request(presentation, "focus", focusContract, { member: "d" });
  await tick(5);
  assert.equal(signals.length, 4);
  client.dispose();
  assert.equal(signals[3]!.aborted, true);
});

test("a reconnect registers the handlers again after the new open, with a higher generation; nothing is replayed", async () => {
  const bridge = installBridge();
  const client = await connectNotes();
  const inputs: unknown[] = [];
  handle(client, "focus", input => { inputs.push(input); });
  await tick();
  assert.equal(bridge.registrations().length, 1);
  const presentation = presentationOf(bridge);
  bridge.setConnected(false);
  // A request of the closed connection that arrives late is lost.
  bridge.request(presentation, "focus", focusContract, { member: "lost" });
  bridge.setConnected(true);
  await tick(5);
  const kinds = bridge.sent.map(message => message["kind"]);
  assert.deepEqual(kinds.slice(kinds.lastIndexOf("hello")), ["hello", "open", "view-requests"]);
  assert.deepEqual(bridge.registrations().at(-1), {
    kind: "view-requests", route: "notes", presentation, generation: 2, requests: [{ name: "focus", contract: focusContract }],
  });
  assert.deepEqual(inputs, []);
  bridge.request(presentation, "focus", focusContract, { member: "new" });
  await tick(5);
  assert.deepEqual(inputs, [{ member: "new" }]);
  client.dispose();
});

test("without view-requests/1 handlers work locally and nothing is sent", async () => {
  const errors = captureErrors();
  try {
    const bridge = installBridge(["text-splice/1"]);
    const client = await connectNotes();
    const stop = handle(client, "focus", () => {});
    handle(client, "toast", () => {});
    stop();
    await tick(5);
    assert.deepEqual(bridge.registrations(), []);
    assert.deepEqual(errors.errors, []);
    client.dispose();
  } finally { errors.restore(); }
});

test("a View without view requests has an empty requests object", async () => {
  installBridge();
  const client = await connectView<State>({ contract: "Tests.Notes:fp-1", route: "notes", members });
  assert.deepEqual(client.requests, {});
  client.dispose();
});

/* ------------------------------------------------------------------ the mock Bridge */

test("mock: raise pushes to every presentation that registered the request, at the route's revision", async () => {
  const bridge = installMockBridge(createMockBridge());
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const view = bridge.view("notes", {
    state: { title: "start" },
    requests: { focus: { contract: focusContract }, toast: {} },
    commands: { touch: state => { view.raise("focus", { member: "title" }); return { title: `${String(state["title"])}!` }; } },
  });
  assert.equal(view.raise("focus", { member: "title" }), 0);
  const first = await connectNotes();
  const second = await connectNotes();
  const inputs: string[] = [];
  handle<{ member: string }>(first, "focus", input => { inputs.push(`first ${input.member} ${first.snapshot.title}`); });
  handle<{ member: string }>(second, "focus", input => { inputs.push(`second ${input.member} ${second.snapshot.title}`); });
  await bridge.flush();
  assert.deepEqual(view.requestHandlers, ["focus"]);
  view.update({ title: "updated" });
  assert.equal(view.raise("focus", { member: "title" }), 2);
  await bridge.flush();
  await tick();
  assert.deepEqual(inputs.sort(), ["first title updated", "second title updated"]);
  // A request raised by a command is pushed after the command's changes.
  inputs.length = 0;
  await first.command("touch");
  await bridge.flush();
  await tick();
  assert.deepEqual(inputs.sort(), ["first title updated!", "second title updated!"]);
  const pushes = bridge.calls.filter(call => call.kind === "view-requests").map(call => call.message["generation"]);
  assert.deepEqual(pushes, [1, 1]);
  first.dispose();
  second.dispose();
  await bridge.flush();
  assert.deepEqual(view.requestHandlers, []);
  assert.equal(view.raise("focus", { member: "title" }), 0);
});

test("mock: a typed mock's requests encode their input and report whether a presentation handles them", async () => {
  const bridge = installMockBridge(createMockBridge());
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const spec: MockTypedViewSpec = {
    kind: "notes", route: "notes", contract: "Tests.Notes:fp-1", fields: {}, defaults: {},
    requests: { focus: { encodeInput: value => ({ member: (value as { member: string }).member.toLowerCase() }) } },
  };
  const mock = mockTypedView(bridge, spec, { state: { title: "start" } }) as {
    readonly requests: { readonly focus: { readonly handled: boolean; raise(input: { member: string }): number } };
  };
  assert.equal(mock.requests.focus.handled, false);
  assert.equal(mock.requests.focus.raise({ member: "Title" }), 0);
  const client = await connectNotes();
  const inputs: unknown[] = [];
  handle(client, "focus", input => { inputs.push(input); });
  await bridge.flush();
  assert.equal(mock.requests.focus.handled, true);
  assert.equal(mock.requests.focus.raise({ member: "Title" }), 1);
  await bridge.flush();
  await tick();
  assert.deepEqual(inputs, [{ member: "title" }]);
  // The registration of a request the View does not have is unsupported.
  const errors = captureErrors();
  try {
    handle(client, "toast", () => {});
    await bridge.flush();
    await tick();
    assert.equal(errors.errors.length, 1);
  } finally { errors.restore(); }
  client.dispose();
});
