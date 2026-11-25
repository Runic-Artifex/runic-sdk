// An interaction as state (#1319): the open requests of one handler, driven by the generated mock.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  createInteractionController, interactionSurface, type InteractionInput, type InteractionName, type InteractionOutput,
  type InteractionRequest, type ViewClient,
} from "../dist/index.js";
import { connectView, defineInteractions } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockTypedInteraction, type MockTypedView } from "../dist/mock.js";
import { mockTypedView } from "../dist/generated-mock.js";
import type { BridgeInteractionContext } from "../dist/interactions.js";
import { clock } from "../dist/clock.js";

type State = { readonly title: string };
type Discard = { readonly title: string };
// The shapes the generator emits for a View with `Interaction<DiscardInput, bool> ConfirmDiscard`.
interface EditorClient extends ViewClient<State> {
  readonly interactions: {
    readonly confirmDiscard: {
      handle(handler: (input: Discard, context: BridgeInteractionContext) => boolean | Promise<boolean>): () => void;
    };
    // `Interaction<Unit, bool> ConfirmQuit`: an input without content.
    readonly confirmQuit: {
      handle(handler: (input: void, context: BridgeInteractionContext) => boolean | Promise<boolean>): () => void;
    };
  };
}
interface EditorMock extends MockTypedView<State, EditorClient> {
  readonly interactions: {
    readonly confirmDiscard: MockTypedInteraction<Discard, boolean>;
    readonly confirmQuit: MockTypedInteraction<void, boolean>;
  };
}
const contract = "Tests.EditorViewModel:I1";

function mockEditor(): EditorMock {
  const bridge = installMockBridge(createMockBridge());
  return mockTypedView(bridge, {
    kind: "editor", route: "editor", contract, defaults: {},
    fields: { title: { encode: value => value, decode: wire => bridgeWire.string(wire) } },
    interactions: {
      confirmDiscard: { encodeInput: value => value, decodeOutput: wire => bridgeWire.boolean(wire) },
      confirmQuit: { encodeInput: () => null, decodeOutput: wire => bridgeWire.boolean(wire) },
    },
  }, { state: { title: "a" } }) as EditorMock;
}
const connect = () => connectView<State>({
  contract, route: "editor", members: { title: bridgeWire.string },
  interactions: defineInteractions({
    confirmDiscard: {
      contract: `${contract}:interaction:ConfirmDiscard`,
      decodeInput: value => bridgeWire.object(value, input => ({ title: bridgeWire.string(input["title"]) })),
      encodeOutput: value => value,
    },
    confirmQuit: { contract: `${contract}:interaction:ConfirmQuit`, decodeInput: () => undefined, encodeOutput: value => value },
  }),
}) as Promise<unknown> as Promise<EditorClient>;

async function until(condition: () => boolean, message: string): Promise<void> {
  for (let round = 0; !condition(); round++) {
    if (round > 1000) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 2));
  }
}

// The names, input and answer come from the generated client.
const typed: [InteractionName<EditorClient>, InteractionInput<EditorClient, "confirmDiscard">, InteractionOutput<EditorClient, "confirmDiscard">]
  = ["confirmDiscard", { title: "x" }, true];
void typed;

type Request = InteractionRequest<Discard, boolean>;

async function setup() {
  const mock = mockEditor();
  const client = await connect();
  const discard = createInteractionController<Discard, boolean>();
  const changes: (Request | undefined)[] = [];
  discard.subscribe(() => changes.push(discard.request));
  discard.setSurface(interactionSurface(client, "confirmDiscard"));
  await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
  return { mock, client, discard, changes };
}

test("the open request is a handle with its input, until its respond answers it (#1319)", async () => {
  const { mock, client, discard, changes } = await setup();
  assert.equal(discard.request, undefined);
  const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const request = discard.request!;
  assert.deepEqual(request.input, { title: "Draft" });
  assert.equal(discard.request, request, "the same handle while the request is open");
  request.respond(true);
  assert.equal(discard.request, undefined);
  assert.deepEqual(await reply, { kind: "answered", output: true });
  assert.deepEqual(changes, [request, undefined]);
  discard.dispose();
  client.dispose();
});

test("a double click answers one request; the next one stays open", async () => {
  const { mock, client, discard } = await setup();
  const first = mock.interactions.confirmDiscard.request({ title: "First" });
  const second = mock.interactions.confirmDiscard.request({ title: "Second" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const shown = discard.request!;
  assert.deepEqual(shown.input, { title: "First" });
  shown.respond(true);
  shown.respond(true);
  assert.deepEqual(await first, { kind: "answered", output: true });
  await until(() => discard.request?.input.title === "Second", "The second request was not shown.");
  shown.respond(true);
  let settled = false;
  void second.then(() => { settled = true; });
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(settled, false, "the second click did not answer the second request");
  discard.request!.respond(false);
  assert.deepEqual(await second, { kind: "answered", output: false });
  discard.dispose();
  client.dispose();
});

test("a handle taken before an abort is ignored after it, and does not answer the next request", async () => {
  const { mock, client, discard } = await setup();
  const cancel = new AbortController();
  const superseded = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
  const next = mock.interactions.confirmDiscard.request({ title: "Next" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const stale = discard.request!;
  assert.deepEqual(stale.input, { title: "Superseded" });
  cancel.abort();
  await until(() => discard.request?.input.title === "Next", "The next request was not shown.");
  stale.respond(true);
  assert.deepEqual(discard.request?.input, { title: "Next" }, "a late answer does not reach the next request");
  assert.deepEqual(await superseded, { kind: "cancelled" });
  discard.request!.respond(false);
  assert.deepEqual(await next, { kind: "answered", output: false });

  // The deadline ends a request too: the page's clock passes it.
  const saved = { set: clock.set, clear: clock.clear, now: clock.now };
  const timers = new Set<{ readonly at: number; readonly callback: () => void }>();
  let offset = 0;
  clock.now = () => Date.now() + offset;
  clock.set = (callback, milliseconds) => {
    if (milliseconds < 60_000) return { real: saved.set(callback, milliseconds) };
    const timer = { at: clock.now() + milliseconds, callback };
    timers.add(timer);
    return timer;
  };
  clock.clear = handle => {
    const timer = handle as { real?: unknown };
    if (timer.real !== undefined) saved.clear(timer.real); else timers.delete(handle as never);
  };
  try {
    const expired = mock.interactions.confirmDiscard.request({ title: "Too late" }, { timeout: 600_000 });
    await until(() => discard.request !== undefined, "The request was not shown.");
    const late = discard.request!;
    offset += 601_000;
    for (const timer of [...timers]) if (timer.at <= clock.now() && timers.delete(timer)) timer.callback();
    assert.equal(discard.request, undefined);
    late.respond(true);
    assert.deepEqual(await expired, { kind: "cancelled" });
  } finally {
    Object.assign(clock, saved);
  }
  discard.dispose();
  client.dispose();
});

test("open requests are answered in order", async () => {
  const { mock, client, discard } = await setup();
  const first = mock.interactions.confirmDiscard.request({ title: "First" });
  const second = mock.interactions.confirmDiscard.request({ title: "Second" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  assert.deepEqual(discard.request?.input, { title: "First" });
  discard.request!.respond(false);
  await until(() => discard.request?.input.title === "Second", "The second request was not shown.");
  discard.request!.respond(true);
  assert.equal(discard.request, undefined);
  assert.deepEqual(await first, { kind: "answered", output: false });
  assert.deepEqual(await second, { kind: "answered", output: true });
  discard.dispose();
  client.dispose();
});

test("an interaction whose input is empty (Unit) has a handle while its request is open", async () => {
  const mock = mockEditor();
  const client = await connect();
  const quit = createInteractionController<void, boolean>();
  quit.setSurface(interactionSurface(client, "confirmQuit"));
  await until(() => mock.interactions.confirmQuit.handled, "The handler was not registered.");
  const reply = mock.interactions.confirmQuit.request();
  await until(() => quit.request !== undefined, "The request was not shown.");
  assert.equal(quit.request!.input, undefined);
  quit.request!.respond(true);
  assert.equal(quit.request, undefined);
  assert.deepEqual(await reply, { kind: "answered", output: true });
  quit.dispose();
  client.dispose();
});

test("a new client ends the previous client's requests and handles the interaction instead", async () => {
  const { mock, client, discard } = await setup();
  const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const old = discard.request!;
  const next = await connect();
  discard.setSurface(interactionSurface(next, "confirmDiscard"));
  assert.equal(discard.request, undefined, "the request clears at once");
  assert.deepEqual(await open, { kind: "cancelled" });
  const reply = mock.interactions.confirmDiscard.request({ title: "New client" });
  await until(() => discard.request !== undefined, "The new client's request was not shown.");
  old.respond(false);
  // `assert.equal` above narrowed `discard.request` to `undefined`.
  const current = discard.request as Request | undefined;
  assert.deepEqual(current?.input, { title: "New client" }, "the old client's handle answers nothing");
  current!.respond(true);
  assert.deepEqual(await reply, { kind: "answered", output: true });
  // No client: nothing handles it.
  discard.setSurface(interactionSurface(undefined, "confirmDiscard"));
  await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
  discard.dispose();
  next.dispose();
  client.dispose();
});

test("dispose removes the handler and ends the open request", async () => {
  const { mock, client, discard } = await setup();
  const open = mock.interactions.confirmDiscard.request({ title: "Leaving" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const request = discard.request!;
  discard.dispose();
  assert.equal(discard.request, undefined);
  assert.deepEqual(await open, { kind: "cancelled" });
  await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
  request.respond(true);
  assert.throws(() => interactionSurface(client, "missing"), TypeError);
  client.dispose();
});
