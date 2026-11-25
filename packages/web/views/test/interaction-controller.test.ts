// An interaction as state (#1319): the open requests of one handler, driven by the generated mock.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  createInteractionController, interactionSurface, type InteractionInput, type InteractionName, type InteractionOutput, type ViewClient,
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
  };
}
interface EditorMock extends MockTypedView<State, EditorClient> {
  readonly interactions: { readonly confirmDiscard: MockTypedInteraction<Discard, boolean> };
}
const contract = "Tests.EditorViewModel:I1";

function mockEditor(): EditorMock {
  const bridge = installMockBridge(createMockBridge());
  return mockTypedView(bridge, {
    kind: "editor", route: "editor", contract, defaults: {},
    fields: { title: { encode: value => value, decode: wire => bridgeWire.string(wire) } },
    interactions: { confirmDiscard: { encodeInput: value => value, decodeOutput: wire => bridgeWire.boolean(wire) } },
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

async function setup() {
  const mock = mockEditor();
  const client = await connect();
  const discard = createInteractionController<Discard, boolean>();
  const changes: (Discard | undefined)[] = [];
  discard.subscribe(() => changes.push(discard.request));
  discard.setSurface(interactionSurface(client, "confirmDiscard"));
  await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
  return { mock, client, discard, changes };
}

test("a request is the state until it is answered (#1319)", async () => {
  const { mock, client, discard, changes } = await setup();
  assert.equal(discard.request, undefined);
  const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  assert.deepEqual(discard.request, { title: "Draft" });
  assert.equal(discard.pending, 1);
  discard.respond(true);
  assert.equal(discard.request, undefined);
  assert.equal(discard.pending, 0);
  assert.deepEqual(await reply, { kind: "answered", output: true });
  assert.deepEqual(changes, [{ title: "Draft" }, undefined]);
  discard.respond(false);
  assert.equal(changes.length, 2, "an answer without an open request is ignored");
  discard.dispose();
  client.dispose();
});

test("an aborted request clears, and a late answer is ignored", async () => {
  const { mock, client, discard } = await setup();
  const cancel = new AbortController();
  const reply = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
  await until(() => discard.request !== undefined, "The request was not shown.");
  cancel.abort();
  await until(() => discard.request === undefined, "The aborted request stayed.");
  discard.respond(true);
  assert.deepEqual(await reply, { kind: "cancelled" });

  // The deadline ends a request too.
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
    offset += 601_000;
    for (const timer of [...timers]) if (timer.at <= clock.now() && timers.delete(timer)) timer.callback();
    assert.equal(discard.request, undefined);
    assert.deepEqual(await expired, { kind: "cancelled" });
  } finally {
    Object.assign(clock, saved);
  }
  assert.equal(discard.request, undefined);
  discard.dispose();
  client.dispose();
});

test("open requests are answered in order", async () => {
  const { mock, client, discard } = await setup();
  const first = mock.interactions.confirmDiscard.request({ title: "First" });
  const second = mock.interactions.confirmDiscard.request({ title: "Second" });
  await until(() => discard.pending === 2, "The requests were not shown.");
  assert.deepEqual(discard.request, { title: "First" });
  discard.respond(false);
  assert.deepEqual(discard.request, { title: "Second" });
  discard.respond(true);
  assert.equal(discard.request, undefined);
  assert.deepEqual(await first, { kind: "answered", output: false });
  assert.deepEqual(await second, { kind: "answered", output: true });
  discard.dispose();
  client.dispose();
});

test("a new client ends the previous client's requests and handles the interaction instead", async () => {
  const { mock, client, discard } = await setup();
  const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
  await until(() => discard.request !== undefined, "The request was not shown.");
  const next = await connect();
  discard.setSurface(interactionSurface(next, "confirmDiscard"));
  assert.equal(discard.request, undefined, "the request clears at once");
  assert.deepEqual(await open, { kind: "cancelled" });
  discard.respond(true);
  const reply = mock.interactions.confirmDiscard.request({ title: "New client" });
  await until(() => discard.request !== undefined, "The new client's request was not shown.");
  discard.respond(true);
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
  discard.dispose();
  assert.equal(discard.request, undefined);
  assert.deepEqual(await open, { kind: "cancelled" });
  await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
  discard.respond(true);
  assert.throws(() => interactionSurface(client, "missing"), TypeError);
  client.dispose();
});
