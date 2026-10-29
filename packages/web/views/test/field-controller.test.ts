// The field controller (F1): checked-write receipts, commit modes, text forms and validation.
import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { clock } from "../dist/clock.js";
import {
  createFieldController,
  createTextFieldController,
  FieldError,
  type BridgeField,
  type FieldBaseline,
  type FieldWriteOptions,
  type FieldWriteReceipt,
} from "../dist/index.js";

type State = { readonly title: string; readonly step: number; readonly titleErrors: readonly string[] };

interface Call { readonly kind: "set" | "write"; readonly value: unknown; readonly options?: FieldWriteOptions<unknown>; resolve(result?: unknown): void; reject(error: unknown): void }

/** A client whose writes settle when the test says so. */
function fakeClient(initial: State) {
  const calls: Call[] = [];
  const versions = { title: 3, step: 1 };
  const client = {
    snapshot: initial,
    subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
    dispose() {},
    calls,
    set(state: Partial<State>) { client.snapshot = { ...client.snapshot, ...state }; },
    fieldBaseline<K extends "title" | "step">(field: K): FieldBaseline<State[K]> {
      return { value: client.snapshot[field], version: versions[field] };
    },
    versions,
  };
  return client;
}
type Client = ReturnType<typeof fakeClient>;

function call(client: Client, kind: Call["kind"], value: unknown, options?: FieldWriteOptions<unknown>): Promise<unknown> {
  return new Promise((resolve, reject) => client.calls.push({ kind, value, ...(options ? { options } : {}), resolve, reject }));
}

const title: BridgeField<Client, State, string> = {
  name: "title", kind: "text",
  read: state => state.title,
  write: (client, value) => call(client, "set", value),
  checked: (client, value, options) => call(client, "write", value, options) as Promise<FieldWriteReceipt<string>>,
  baseline: client => client.fieldBaseline("title"),
  errors: state => state.titleErrors,
};
const step: BridgeField<Client, State, number> = {
  name: "step", kind: "number", integer: true,
  read: state => state.step,
  write: (client, value) => call(client, "set", value),
  checked: (client, value, options) => call(client, "write", value, options) as Promise<FieldWriteReceipt<number>>,
  baseline: client => client.fieldBaseline("step"),
};

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

function setup<T>(field: BridgeField<Client, State, T>, options: Parameters<typeof createFieldController>[1] = {}) {
  const client = fakeClient({ title: "Draft", step: 1, titleErrors: [] });
  const controller = createFieldController(field, options);
  controller.setSource({ state: client.snapshot, client });
  const changes: unknown[] = [];
  controller.subscribe(() => changes.push(controller.current));
  return { client, controller, changes, refresh: () => controller.setSource({ state: client.snapshot, client }) };
}

// Virtual time for the pause of commit: "pause".
let now = 0;
const timers = new Map<number, { due: number; callback: () => void }>();
const realClock = { set: clock.set, clear: clock.clear, now: clock.now };
beforeEach(() => {
  now = 0;
  timers.clear();
  let id = 0;
  clock.set = (callback, milliseconds) => { timers.set(++id, { due: now + milliseconds, callback }); return id; };
  clock.clear = handle => { timers.delete(handle as number); };
});
afterEach(() => { Object.assign(clock, realClock); });
function advance(milliseconds: number): void {
  now += milliseconds;
  for (const [id, timer] of [...timers].sort((a, b) => a[1].due - b[1].due))
    if (timer.due <= now && timers.delete(id)) timer.callback();
}

test("a checked write is based on the value and version the draft started from, then on each receipt", async () => {
  const { client, controller } = setup(title, { checked: true });
  controller.inputText("Draft 1");
  assert.equal(client.calls.length, 1);
  assert.deepEqual(client.calls[0]!.options!.baseline, { value: "Draft", version: 3 });
  assert.match(client.calls[0]!.options!.requestId, /^[0-9a-f-]{36}$/);
  // Another writer changes the field: the draft keeps its baseline.
  client.versions.title = 4;
  controller.inputText("Draft 12");
  assert.equal(client.calls.length, 1, "checked writes are one at a time");
  client.calls[0]!.resolve({ kind: "applied", snapshot: { value: "Draft 1", version: 5 } });
  await flush();
  assert.equal(client.calls.length, 2);
  assert.deepEqual(client.calls[1]!.options!.baseline, { value: "Draft 1", version: 5 });
  assert.equal(controller.current.editing, true);
  client.set({ title: "Draft 12" });
  client.calls[1]!.resolve({ kind: "applied", snapshot: { value: "Draft 12", version: 6 } });
  await flush();
  assert.deepEqual(controller.current, { value: "Draft 12", text: "Draft 12", editing: false, error: undefined, errors: [], conflict: undefined });
  // The next draft starts from the state's version again.
  client.versions.title = 9;
  controller.inputText("Next");
  assert.deepEqual(client.calls[2]!.options!.baseline, { value: "Draft 12", version: 9 });
});

test("a rejected checked write keeps the draft and its reason; the next input writes again", async () => {
  const { client, controller } = setup(title, { checked: true });
  controller.inputText("Bad");
  client.calls[0]!.resolve({ kind: "rejected", message: "Titles cannot be Bad." });
  await flush();
  assert.equal(controller.current.value, "Bad");
  assert.equal(controller.current.editing, true);
  assert.ok(controller.current.error instanceof FieldError);
  assert.equal((controller.current.error as FieldError).kind, "rejected");
  assert.equal((controller.current.error as FieldError).message, "Titles cannot be Bad.");
  controller.inputText("Good");
  assert.equal(controller.current.error, undefined);
  assert.deepEqual(client.calls[1]!.options!.baseline, { value: "Draft", version: 3 }, "the field did not change");
});

test("a conflict keeps the draft and writes nothing until retry or reset", async () => {
  const { client, controller } = setup(title, { checked: true });
  controller.inputText("Mine");
  client.calls[0]!.resolve({ kind: "conflict", incoming: { value: "Theirs", version: 4 }, message: "The field changed." });
  await flush();
  assert.deepEqual(controller.current.conflict, { value: "Theirs", version: 4 });
  assert.equal((controller.current.error as FieldError).kind, "conflict");
  assert.deepEqual((controller.current.error as FieldError).current, { value: "Theirs", version: 4 });
  assert.equal(controller.current.value, "Mine");
  controller.inputText("Mine too");
  assert.equal(client.calls.length, 1, "no write while the conflict is open");
  assert.equal(controller.current.value, "Mine too");
  // retry: the draft over the producer's value.
  controller.retry();
  assert.equal(client.calls.length, 2);
  assert.equal(client.calls[1]!.value, "Mine too");
  assert.deepEqual(client.calls[1]!.options!.baseline, { value: "Theirs", version: 4 });
  assert.equal(controller.current.conflict, undefined);
  assert.equal(controller.current.error, undefined);
  client.calls[1]!.resolve({ kind: "conflict", incoming: { value: "Again", version: 5 }, message: "Changed again." });
  await flush();
  // reset: take the producer's value.
  client.set({ title: "Again" });
  controller.reset();
  controller.setSource({ state: client.snapshot, client });
  assert.deepEqual(controller.current, { value: "Again", text: "Again", editing: false, error: undefined, errors: [], conflict: undefined });
});

test("a conflict whose incoming value is the value sent is an applied write", async () => {
  const { client, controller } = setup(title, { checked: true });
  controller.inputText("Same");
  client.set({ title: "Same" });
  client.calls[0]!.resolve({ kind: "conflict", incoming: { value: "Same", version: 4 }, message: "Changed." });
  await flush();
  assert.deepEqual(controller.current, { value: "Same", text: "Same", editing: false, error: undefined, errors: [], conflict: undefined });
});

test("committed-with-error drops the draft and reports the error; applied.validation is errors without a state source", async () => {
  const noStateErrors: BridgeField<Client, State, string> = { ...title };
  delete (noStateErrors as { errors?: unknown }).errors;
  const { client, controller } = setup(noStateErrors, { checked: true });
  controller.inputText("Half");
  client.set({ title: "Half" });
  client.calls[0]!.resolve({ kind: "committed-with-error", snapshot: { value: "Half", version: 4 }, message: "Saving failed." });
  await flush();
  assert.equal(controller.current.editing, false);
  assert.equal(controller.current.value, "Half");
  assert.equal((controller.current.error as FieldError).kind, "committed-with-error");
  assert.deepEqual((controller.current.error as FieldError).current, { value: "Half", version: 4 });
  controller.inputText("x");
  client.set({ title: "x" });
  client.calls[1]!.resolve({ kind: "applied", snapshot: { value: "x", version: 5 }, validation: "Too short.\nNo capital." });
  await flush();
  assert.equal(controller.current.error, undefined);
  assert.deepEqual(controller.current.errors, ["Too short.", "No capital."]);
});

test("errors follow the member's validation in the View's state", () => {
  const { client, controller, refresh } = setup(title);
  assert.deepEqual(controller.current.errors, []);
  client.set({ titleErrors: ["Title required"] });
  refresh();
  assert.deepEqual(controller.current.errors, ["Title required"]);
  const before = controller.current;
  client.set({ titleErrors: ["Title required"] });
  refresh();
  assert.equal(controller.current, before, "equal messages keep the state");
});

test("a text field's checked write that is rejected keeps the draft (useTextField with a checked write)", async () => {
  const client = fakeClient({ title: "Draft", step: 1, titleErrors: [] });
  const field = createTextFieldController<State, Client>({
    read: state => state.title,
    write: (target, value) => call(target, "write", value),
  });
  field.setSource({ state: client.snapshot, client });
  field.input("Rejected");
  client.calls[0]!.resolve({ kind: "rejected", message: "No." });
  await flush();
  assert.equal(field.current.value, "Rejected");
  assert.equal(field.current.editing, true);
  assert.equal((field.current.error as FieldError).kind, "rejected");
});

test("commit: pause writes after the pause and on commit, and when disposed", async () => {
  const { client, controller } = setup(title, { commit: "pause", delay: 500 });
  controller.inputText("D");
  advance(300);
  controller.inputText("Dr");
  advance(300);
  assert.equal(client.calls.length, 0, "the pause restarts with each input");
  assert.equal(controller.current.editing, true);
  advance(200);
  assert.deepEqual(client.calls.map(call => call.value), ["Dr"]);
  client.calls[0]!.resolve();
  await flush();
  controller.inputText("Dra");
  controller.commit();
  assert.deepEqual(client.calls.map(call => call.value), ["Dr", "Dra"], "commit writes at once");
  controller.inputText("Drafted");
  controller.dispose();
  assert.deepEqual(client.calls.map(call => call.value), ["Dr", "Dra", "Drafted"]);
});

test("commit: pause keeps the draft until the written value settled", async () => {
  const { client, controller } = setup(title, { commit: "pause", delay: 100 });
  controller.inputText("A");
  advance(100);
  controller.inputText("AB");
  client.set({ title: "A" });
  client.calls[0]!.resolve();
  await flush();
  assert.equal(controller.current.value, "AB", "an unwritten draft stays");
  assert.equal(controller.current.editing, true);
  advance(100);
  client.set({ title: "AB" });
  client.calls[1]!.resolve();
  await flush();
  assert.equal(controller.current.editing, false);
});

test("a number field parses its text, keeps text that does not parse, and shows what was typed", async () => {
  const { client, controller } = setup(step);
  controller.inputText("-");
  assert.equal(client.calls.length, 0);
  assert.equal(controller.current.text, "-");
  assert.equal(controller.current.value, 1);
  assert.equal(controller.current.editing, true);
  assert.equal((controller.current.error as FieldError).kind, "invalid-text");
  controller.inputText("-2");
  assert.equal(controller.current.error, undefined);
  assert.deepEqual(client.calls.map(call => call.value), [-2]);
  controller.inputText("2.5");
  assert.equal((controller.current.error as unknown as FieldError).kind, "invalid-text", "an integer field");
  controller.inputText("10.");
  client.set({ step: -2 });
  client.calls[0]!.resolve();
  await flush();
  assert.deepEqual(client.calls.map(call => call.value), [-2, 10]);
  client.set({ step: 10 });
  client.calls[1]!.resolve();
  await flush();
  assert.equal(controller.current.editing, false);
  assert.equal(controller.current.value, 10);
  assert.equal(controller.current.text, "10.", "the typed text still means the value");
  controller.commit();
  assert.equal(controller.current.text, "10", "formatted once the user is done");
  // Another writer: the text follows the value.
  controller.inputText("10.");
  client.set({ step: 7 });
  client.calls[2]!.resolve();
  await flush();
  assert.equal(controller.current.text, "7");
});

test("choice, boolean and nullable fields parse their text forms", () => {
  type Mode = "plain" | "fancy";
  const parseWith = <T>(field: Partial<BridgeField<Client, State, T>>, text: string): unknown => {
    const values: unknown[] = [];
    const controller = createFieldController<State, Client, T>({ read: () => undefined as T, write: (_client, value) => { values.push(value); return Promise.resolve(); }, ...field });
    controller.setSource({ state: { title: "", step: 0, titleErrors: [] }, client: fakeClient({ title: "", step: 0, titleErrors: [] }) });
    controller.inputText(text);
    return controller.current.error instanceof FieldError ? controller.current.error.kind : values[0];
  };
  assert.equal(parseWith<Mode>({ kind: "choice", choices: ["plain", "fancy"] }, "fancy"), "fancy");
  assert.equal(parseWith<Mode>({ kind: "choice", choices: ["plain", "fancy"] }, "other"), "invalid-text");
  assert.equal(parseWith<Mode | null>({ kind: "choice", nullable: true, choices: ["plain", "fancy"] }, ""), null);
  assert.equal(parseWith<boolean>({ kind: "boolean" }, "true"), true);
  assert.equal(parseWith<number | null>({ kind: "number", nullable: true }, " "), null);
  assert.equal(parseWith<number>({ kind: "number" }, ""), "invalid-text");
  assert.equal(parseWith<number>({ kind: "number" }, "1e3"), 1000);
  assert.equal(parseWith<bigint>({ kind: "bigint" }, "9007199254740993"), 9007199254740993n);
  assert.equal(parseWith<bigint>({ kind: "bigint" }, "1.5"), "invalid-text");
  assert.equal(parseWith<unknown>({ kind: "value" }, "{}"), "invalid-text");
});

test("a checked field needs the checked write; options are validated", () => {
  assert.throws(() => createFieldController({ read: () => "", write: async () => undefined }, { checked: true }), TypeError);
  assert.throws(() => createFieldController(title, { commit: "blur" as "input" }), RangeError);
  assert.throws(() => createFieldController(title, { delay: -1 }), RangeError);
});

test("FieldError is recognized across package copies", () => {
  const error = new FieldError("rejected", "No.");
  assert.ok(error instanceof FieldError);
  assert.ok(error instanceof Error);
  assert.equal(Object.assign(Object.create(Error.prototype), { [Symbol.for("runic.views.FieldError")]: true }) instanceof FieldError, true);
});

test("a checked field against the mock bridge: applied, then a conflict with another writer", async () => {
  Object.assign(clock, realClock);
  const { createMockBridge, installMockBridge } = await import("../dist/mock.js");
  const { connectView } = await import("../dist/generated.js");
  const wire = await import("../dist/wire.js");
  const bridge = installMockBridge(createMockBridge());
  bridge.view("form", { state: { title: "a" }, checkedFields: ["title"] });
  type FormState = { readonly title: string };
  const view = await connectView<FormState>({ contract: "Tests.Form:fp", route: "form", members: { title: wire.string }, checkedFields: ["title"] });
  const field: BridgeField<typeof view, FormState, string> = {
    name: "title", kind: "text", read: state => state.title,
    write: (client, value) => client.set("title", value),
    checked: (client, value, options) => client.write("title", {
      requestId: options.requestId, expectedVersion: options.baseline.version, expectedValue: options.baseline.value, value,
    }, value => wire.string(value)),
    baseline: client => client.fieldBaseline("title") as FieldBaseline<string>,
  };
  const controller = createFieldController(field, { checked: true, commit: "pause" });
  view.subscribe(state => controller.setSource({ state, client: view }));
  const until = async (condition: () => boolean) => {
    for (let round = 0; !condition(); round++) {
      if (round > 500) assert.fail("timed out");
      await new Promise(resolve => setTimeout(resolve, 2));
    }
  };
  controller.inputText("b");
  controller.commit();
  await until(() => !controller.current.editing);
  assert.deepEqual(controller.current, { value: "b", text: "b", editing: false, error: undefined, errors: [], conflict: undefined });
  // Another writer changes the field after the draft started: the write conflicts and keeps the draft.
  controller.inputText("c");
  await view.set("title", "other");
  controller.commit();
  await until(() => controller.current.conflict !== undefined);
  assert.equal(controller.current.value, "c");
  assert.equal((controller.current.conflict as FieldBaseline<string> | undefined)?.value, "other");
  controller.reset();
  assert.deepEqual(controller.current, { value: "other", text: "other", editing: false, error: undefined, errors: [], conflict: undefined });
  controller.dispose();
  view.dispose();
});

test("commit() resolves once the field's writes settled, so Save can follow them", async () => {
  const { client, controller } = setup(title, { commit: "pause", delay: 100 });
  let committed = false;
  await controller.commit();
  controller.inputText("A");
  advance(100);
  controller.inputText("AB");
  const done = controller.commit().then(() => { committed = true; });
  assert.deepEqual(client.calls.map(call => call.value), ["A"], "AB waits behind the write in flight");
  client.calls[0]!.resolve();
  await flush();
  assert.equal(committed, false);
  assert.deepEqual(client.calls.map(call => call.value), ["A", "AB"]);
  client.calls[1]!.reject(new Error("offline"));
  await done;
  assert.equal(committed, true);
  assert.equal(((await controller.commit()).error as Error).message, "offline", "commit resolves with the state");
  assert.equal((controller.current.error as Error).message, "offline", "commit never rejects; the error is in the state");
  // A conflict does not hold commit().
  const checkedField = setup(title, { checked: true });
  checkedField.controller.inputText("Mine");
  const pending = checkedField.controller.commit();
  checkedField.client.calls[0]!.resolve({ kind: "conflict", incoming: { value: "Theirs", version: 4 }, message: "Changed." });
  await pending;
  checkedField.controller.inputText("Mine 2");
  await checkedField.controller.commit();
});
