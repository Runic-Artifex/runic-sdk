// Typing key by key never loses a character (#673): the setter overlay of `snapshot` (D051.12) and
// `createTextFieldController`, against a mock whose replies and pushes are slow and whose requests run out of order.
import assert from "node:assert/strict";
import { test } from "node:test";
import { createTextFieldController, type ViewClient } from "../dist/index.js";
import { connectView, type ViewConnection } from "../dist/generated.js";
import { createMockBridge, installMockBridge, type MockBridge, type MockBridgeOptions } from "../dist/mock.js";
import * as wire from "../dist/wire.js";

type State = { readonly title: string; readonly saved: number };
const sentence = "Typing key by key never loses a character.";

async function connect(options: MockBridgeOptions, setter?: (value: string) => string): Promise<{ bridge: MockBridge; client: ViewConnection<State> }> {
  const bridge = installMockBridge(createMockBridge(options));
  bridge.view("editor", {
    state: { title: "", saved: 0 },
    set: setter ? { title: (_state, value) => ({ title: setter(value as string) }) } : {},
    commands: { rename: (_state, input) => ({ title: input as string }) },
  });
  const pending = connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string, saved: value => wire.integer(value, 0, 1000) } });
  return { bridge, client: await drive(bridge, pending) };
}

/** Moves virtual time until `promise` settles, as the slow mock needs. */
async function drive<T>(bridge: MockBridge, promise: Promise<T>): Promise<T> {
  let settled = false;
  const observed = promise.finally(() => { settled = true; });
  for (let round = 0; round < 200 && !settled; round++) await bridge.advance(50);
  return observed;
}

/** An input element bound to a value: it shows what the binding last rendered. */
class Input {
  value = "";
  readonly shown: string[] = [];
  render(value: string): void { this.value = value; this.shown.push(value); }
}

const settle = async (bridge: MockBridge, until: () => boolean) => {
  for (let round = 0; round < 200 && !until(); round++) await bridge.advance(50);
  assert.ok(until(), "the field did not settle");
};

for (const reorderRequests of [false, true]) {
  test(`a plain setter binding keeps every keystroke with slow replies${reorderRequests ? " and reordered requests" : ""}`, async () => {
    const { bridge, client } = await connect({ scheduling: "manual", latency: "slow", reorderRequests });
    const input = new Input();
    // `<input [value]="state.title" (input)="setTitle($event.target.value)">`: each state is rendered, each key sets.
    const stop = client.subscribe(state => input.render(state.title));
    let typed = "";
    const calls: Promise<unknown>[] = [];
    for (const character of sentence) {
      typed = input.value + character;
      input.value = typed;
      calls.push(client.set("title", typed));
      // An echo of an older edit never replaces what the user typed.
      assert.equal(input.value, typed);
      await bridge.advance(Math.floor(Math.random() * 40));
      assert.equal(input.value, typed);
    }
    await drive(bridge, Promise.all(calls));
    await bridge.advance(500);
    assert.equal(input.value, sentence);
    assert.equal(client.snapshot.title, sentence);
    assert.equal(bridge.calls.filter(call => call.kind === "set").length, sentence.length);
    // Every rendered value is a prefix of the sentence that the user had typed by then: none lost a character.
    for (const shown of input.shown) assert.ok(sentence.startsWith(shown), shown);
    stop();
    client.dispose();
  });
}

for (const coalesce of [true, false]) {
  test(`a text field controller keeps every keystroke with slow, reordered replies (coalesce: ${coalesce})`, async () => {
    const { bridge, client } = await connect({ scheduling: "manual", latency: { min: 20, max: 150 }, reorderRequests: true });
    const input = new Input();
    const field = createTextFieldController<State, ViewClient<State>>({
      read: state => state.title, write: (view, value) => (view as ViewConnection<State>).set("title", value), coalesce,
    });
    const source = (state: State) => field.setSource({ state, client });
    const stop = client.subscribe(state => { source(state); input.render(field.current.value); });
    field.subscribe(() => input.render(field.current.value));
    for (const character of sentence) {
      field.input(input.value + character);
      assert.ok(field.current.editing);
      await bridge.advance(Math.floor(Math.random() * 30));
    }
    await settle(bridge, () => !field.current.editing);
    assert.equal(input.value, sentence);
    assert.equal(client.snapshot.title, sentence);
    for (const shown of input.shown) assert.ok(sentence.startsWith(shown), shown);
    const sets = bridge.calls.filter(call => call.kind === "set").length;
    if (coalesce) assert.ok(sets < sentence.length, `${sets} writes for ${sentence.length} keys`);
    else assert.equal(sets, sentence.length);
    stop();
    field.dispose();
    client.dispose();
  });
}

test("a coalescing controller has at most one write in flight and writes the latest value next", async () => {
  const { bridge, client } = await connect({ scheduling: "manual", latency: 100 });
  const written: string[] = [];
  let inFlight = 0;
  let most = 0;
  const field = createTextFieldController<State, ViewConnection<State>>({
    read: state => state.title,
    write: async (view, value) => {
      written.push(value);
      most = Math.max(most, ++inFlight);
      try { return await view.set("title", value); } finally { inFlight--; }
    },
  });
  field.setSource({ state: client.snapshot, client });
  for (const value of ["a", "ab", "abc", "abcd"]) field.input(value);
  await settle(bridge, () => !field.current.editing);
  assert.deepEqual(written, ["a", "abcd"]);
  assert.equal(most, 1);
  assert.deepEqual(field.current, { value: "abcd", text: "abcd", editing: false, error: undefined, errors: [], conflict: undefined });
});

test("the field shows the View's normalization once the last write settled", async () => {
  const { bridge, client } = await connect({ scheduling: "manual", latency: 50 }, value => value.trim());
  const field = createTextFieldController<State, ViewConnection<State>>({ read: state => state.title, write: (view, value) => view.set("title", value) });
  field.setSource({ state: client.snapshot, client });
  field.input(" a");
  field.input(" a ");
  // While writes are in flight the field keeps the draft, untrimmed.
  await bridge.advance(10);
  assert.equal(field.current.value, " a ");
  await settle(bridge, () => !field.current.editing);
  assert.equal(field.current.value, "a");
});

test("IME composition is written once it ends", async () => {
  const { bridge, client } = await connect({ scheduling: "manual" });
  const field = createTextFieldController<State, ViewConnection<State>>({ read: state => state.title, write: (view, value) => view.set("title", value) });
  field.setSource({ state: client.snapshot, client });
  field.compositionStart();
  field.input("ｋ");
  field.input("か");
  await bridge.flush();
  assert.equal(bridge.calls.filter(call => call.kind === "set").length, 0);
  assert.deepEqual(field.current, { value: "か", text: "か", editing: true, error: undefined, errors: [], conflict: undefined });
  field.compositionEnd("漢");
  await settle(bridge, () => !field.current.editing);
  assert.deepEqual(bridge.calls.filter(call => call.kind === "set").map(call => call.message["value"]), ["漢"]);
  assert.equal(field.current.value, "漢");
});

test("a failed write keeps the draft with its error until reset", async () => {
  const { bridge, client } = await connect({ scheduling: "manual" }, value => {
    if (value.length > 3) throw Object.assign(new Error("Too long."), { kind: "rejected", reason: "invalid-input" });
    return value;
  });
  const field = createTextFieldController<State, ViewConnection<State>>({ read: state => state.title, write: (view, value) => view.set("title", value) });
  field.setSource({ state: client.snapshot, client });
  let notified = 0;
  field.subscribe(() => notified++);
  field.input("abcd");
  await bridge.flush();
  await bridge.flush();
  assert.equal(field.current.value, "abcd");
  assert.equal(field.current.editing, true);
  assert.equal((field.current.error as { reason?: string }).reason, "invalid-input");
  // The overlay ended with the failed edit: the View shows the producer's value.
  assert.equal(client.snapshot.title, "");
  field.reset();
  assert.deepEqual(field.current, { value: "", text: "", editing: false, error: undefined, errors: [], conflict: undefined });
  assert.ok(notified >= 2);
});

test("without keepDraftOnError a failed write shows the View's value and the error", async () => {
  const { bridge, client } = await connect({ scheduling: "manual" }, () => { throw Object.assign(new Error("No."), { kind: "rejected" }); });
  const field = createTextFieldController<State, ViewConnection<State>>({
    read: state => state.title, write: (view, value) => view.set("title", value), keepDraftOnError: false,
  });
  field.setSource({ state: client.snapshot, client });
  field.input("x");
  await bridge.flush();
  await bridge.flush();
  assert.equal(field.current.value, "");
  assert.equal(field.current.editing, false);
  assert.ok(field.current.error !== undefined);
});

test("a command can be the write, and a value typed before a client exists is written once one does", async () => {
  const { bridge, client } = await connect({ scheduling: "manual" });
  const field = createTextFieldController<State, ViewConnection<State>>({ read: state => state.title, write: (view, value) => view.command("rename", value) });
  field.input("early");
  assert.equal(bridge.calls.filter(call => call.kind === "command").length, 0);
  field.setSource({ state: client.snapshot, client });
  await settle(bridge, () => !field.current.editing);
  assert.equal(client.snapshot.title, "early");
  assert.equal(field.current.value, "early");
});

test("dispose writes the value that waited for a write in flight", async () => {
  const { bridge, client } = await connect({ scheduling: "manual", latency: 100 });
  const field = createTextFieldController<State, ViewConnection<State>>({ read: state => state.title, write: (view, value) => view.set("title", value) });
  field.setSource({ state: client.snapshot, client });
  field.input("a");
  field.input("ab");
  field.dispose();
  for (let round = 0; round < 20; round++) await bridge.advance(100);
  assert.equal(client.snapshot.title, "ab");
});
