// Forms (C2): createFormController's state and submit, and the page's field drafts written before a window close.
import assert from "node:assert/strict";
import { test } from "node:test";
import { liveFieldCount } from "../dist/field-controller.js";
import {
  commitAllFields,
  createFieldController,
  createFormController,
  FieldError,
  formStateOf,
  type BridgeValidationState,
  type FieldState,
} from "../dist/index.js";
import { connectView, fieldValidating } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type State = { readonly title: string; readonly step: number; readonly validation: BridgeValidationState; readonly isDirty: boolean };
const valid: BridgeValidationState = { hasErrors: false, truncated: false, errors: [] };

/** A client whose writes settle when the test says so; a settled write applies its value. */
function fakeClient() {
  const writes: { member: keyof State; value: unknown; resolve(): void; reject(error: unknown): void }[] = [];
  const client = {
    snapshot: { title: "", step: 0, validation: valid, isDirty: false } as State,
    writes,
    write(member: keyof State, value: unknown): Promise<unknown> {
      return new Promise((resolve, reject) => writes.push({
        member, value,
        resolve() { client.snapshot = { ...client.snapshot, [member]: value, isDirty: true }; resolve(undefined); },
        reject,
      }));
    },
  };
  return client;
}
type Client = ReturnType<typeof fakeClient>;

function fields(client: Client) {
  const title = createFieldController<State, Client, string>({
    kind: "text", read: state => state.title, write: (target, value) => target.write("title", value),
    errors: state => state.validation.errors.filter(error => error.path[0] === "title").map(error => error.message),
    validating: state => fieldValidating(state.validation, "title"),
  }, { commit: "pause", delay: 60_000 });
  const step = createFieldController<State, Client, number>({
    kind: "number", read: state => state.step, write: (target, value) => target.write("step", value),
  });
  const feed = () => { for (const field of [title, step] as const) field.setSource({ state: client.snapshot, client } as never); };
  feed();
  return { title, step, feed };
}
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

test("a form is dirty, pending and valid from its fields and the View's dirty flag", async () => {
  const client = fakeClient();
  const { title, step, feed } = fields(client);
  const form = createFormController({ title, step }, { dirty: () => client.snapshot.isDirty });
  const notified: FormStateSnapshot[] = [];
  type FormStateSnapshot = ReturnType<typeof formStateOf>;
  form.subscribe(() => notified.push(form.current));
  assert.deepEqual(form.current, { dirty: false, editing: false, validating: false, pending: false, valid: true, submitting: false });
  assert.equal(form.current, form.current, "the same state object while nothing changed");

  title.inputText("Draft");
  assert.deepEqual([form.current.dirty, form.current.editing, form.current.pending], [true, true, true], "a draft not written yet is dirty");
  assert.equal(client.writes.length, 0, "commit: pause holds the draft");
  assert.ok(notified.length > 0);

  // The View validates and checks the title asynchronously.
  client.snapshot = { ...client.snapshot, validation: { hasErrors: true, truncated: false, errors: [{ path: ["title"], message: "Too short." }], pending: [["title"]] } };
  feed();
  assert.deepEqual([form.current.valid, form.current.validating], [false, true]);

  const committed = form.commit();
  assert.deepEqual(client.writes.map(write => write.value), ["Draft"], "commit writes the pause draft");
  client.writes[0]!.resolve();
  await committed;
  client.snapshot = { ...client.snapshot, validation: valid };
  feed();
  assert.deepEqual(form.current, { dirty: true, editing: false, validating: false, pending: false, valid: true, submitting: false },
    "the View's isDirty keeps the form dirty after the draft was written");

  step.inputText("x");
  assert.equal(form.current.valid, false, "a text that does not parse is invalid");
  form.reset();
  assert.equal(form.current.valid, true);
  form.dispose();
  title.dispose();
  step.dispose();
});

test("submit writes every field first, then runs the command; a failed field skips it; a second submit is busy", async () => {
  const client = fakeClient();
  const { title, step } = fields(client);
  const form = createFormController([title, step]);
  title.inputText("Saved title");
  const order: string[] = [];
  const submitted = form.submit(() => { order.push(`save after ${client.writes.length} writes`); return 42; });
  assert.equal(form.current.submitting, true);
  assert.deepEqual(await form.submit(() => order.push("second")), { status: "busy" });
  assert.equal(client.writes.length, 1, "the title's pause draft is written before the command");
  client.writes[0]!.resolve();
  assert.deepEqual(await submitted, { status: "submitted", result: 42 });
  assert.deepEqual(order, ["save after 1 writes"]);
  assert.equal(form.current.submitting, false);

  step.inputText("7");
  client.writes[1]!.reject(new Error("The step could not be written."));
  await settle();
  // The kept draft is written again by the submit; it fails again, so the command does not run.
  const failing = form.submit(() => order.push("not run"));
  await settle();
  assert.deepEqual(client.writes.map(write => write.value), ["Saved title", 7, 7], "submit retries the kept draft");
  client.writes[2]!.reject(new Error("The step could not be written."));
  const failed = await failing;
  assert.equal(failed.status, "field-failed");
  assert.equal((failed as { error: Error }).error.message, "The step could not be written.");
  assert.deepEqual(order, ["save after 1 writes"]);

  step.inputText("8.5x");
  const invalidText = await form.submit(() => order.push("not run either"));
  assert.equal(invalidText.status, "field-failed");
  assert.ok((invalidText as { error: unknown }).error instanceof FieldError);
  title.dispose();
  step.dispose();
});

test("formStateOf combines the states of framework field handles", () => {
  const base: Pick<FieldState<unknown>, "editing" | "validating" | "errors" | "error"> = { editing: false, validating: false, errors: [], error: undefined };
  assert.deepEqual(formStateOf([base, { ...base, validating: true }], false, false),
    { dirty: false, editing: false, validating: true, pending: true, valid: true, submitting: false });
  assert.deepEqual(formStateOf([base, { ...base, error: new Error("x") }], true, true),
    { dirty: true, editing: false, validating: false, pending: true, valid: false, submitting: true });
});

test("commitAllFields writes the page's pause drafts, and a window close writes them before it is acknowledged", async () => {
  const client = fakeClient();
  const { title, step } = fields(client);
  title.inputText("Unsent");
  // Page-wide: other tests' fields may hold drafts too, so this test does not wait for all of them.
  void commitAllFields();
  assert.deepEqual(client.writes.map(write => write.value), ["Unsent"]);
  client.writes[0]!.resolve();
  await settle();
  assert.equal(title.current.editing, false);
  title.dispose();
  step.dispose();

  const host = globalThis as unknown as Record<symbol, { dispose?: () => void } | undefined>;
  const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
  host[runtimeKey]?.dispose?.();
  delete host[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("editor", { state: { title: "" } });
  try {
    const editor = await connectView<{ readonly title: string }>({ contract: "Tests.Editor:fp-1", route: "editor", members: { title: bridgeWire.string } });
    const field = createFieldController({ kind: "text", read: (state: { readonly title: string }) => state.title, write: (target: typeof editor, value: string) => target.set("title", value) },
      { commit: "pause", delay: 60_000 });
    editor.subscribe(state => field.setSource({ state, client: editor }));
    field.inputText("Typed before close");
    await settle();
    assert.equal(bridge.calls.some(call => call.kind === "set"), false, "the pause draft waits");
    // Other tests' fields may hold drafts that never settle: the close waits for them up to its 2-second limit.
    assert.equal(await bridge.closeWindow(4_000), true);
    const kinds = bridge.calls.map(call => call.kind);
    assert.ok(kinds.includes("set") && kinds.indexOf("set") < kinds.indexOf("closing-reply"), `the draft is written before the close is acknowledged: ${kinds.join(", ")}`);
    field.dispose();
    editor.dispose();
  } finally {
    host[runtimeKey]?.dispose?.();
    delete host[runtimeKey];
  }
});

test("submit (C2 review): a committed-with-error write does not block, a transport failure is retried, an old failure without a draft does not block", async () => {
  type Receipt = { kind: string; snapshot?: { value: unknown; version: number }; message?: string };
  const results: ((receipt: Receipt | Error) => void)[] = [];
  let value = "";
  const field = createFieldController<{ readonly title: string }, object, string>({
    kind: "text", read: state => state.title,
    write: (_client, next) => new Promise((resolve, reject) => results.push(result => {
      if (result instanceof Error) reject(result); else { value = next; resolve(result); }
    })),
  }, { keepDraftOnError: true });
  const feed = () => field.setSource({ state: { title: value }, client: {} });
  feed();
  const form = createFormController([field]);
  // committed-with-error: the value reached the View; the error stays, but later submits run.
  field.inputText("a");
  results[0]!({ kind: "committed-with-error", snapshot: { value: "a", version: 2 }, message: "Saved, but the index failed." });
  await settle();
  assert.equal((field.current.error as FieldError).kind, "committed-with-error");
  assert.deepEqual(await form.submit(() => "ran"), { status: "submitted", result: "ran" });
  assert.deepEqual(await form.submit(() => "again"), { status: "submitted", result: "again" });

  // A transport failure keeps the draft; the next submit writes it again and runs once that write succeeded.
  field.inputText("b");
  results[1]!(new Error("disconnected"));
  await settle();
  assert.equal(field.current.editing, true);
  const retried = form.submit(() => `saved ${value}`);
  await settle();
  assert.equal(results.length, 3, "the draft is written again");
  results[2]!({ kind: "applied", snapshot: { value: "b", version: 3 } });
  assert.deepEqual(await retried, { status: "submitted", result: "saved b" });

  // A rejected checked write blocks: the value did not reach the View.
  field.inputText("c");
  results[3]!({ kind: "rejected", message: "No." });
  await settle();
  const rejected = await form.submit(() => "not run");
  assert.equal(rejected.status, "field-failed");
  assert.equal(((rejected as { error: FieldError }).error).kind, "rejected");
  field.dispose();

  // An earlier transport failure that left no draft (keepDraftOnError: false) does not block a later submit.
  const loose = createFieldController<{ readonly title: string }, object, string>({
    kind: "text", read: state => state.title, write: () => Promise.reject(new Error("offline")),
  }, { keepDraftOnError: false });
  loose.setSource({ state: { title: "" }, client: {} });
  loose.inputText("x");
  await settle();
  assert.equal(loose.current.editing, false);
  assert.ok(loose.current.error instanceof Error);
  assert.deepEqual(await createFormController([loose]).submit(() => "ok"), { status: "submitted", result: "ok" });
  loose.dispose();
});

test("submit waits for pending validation with waitForValidation, and gives up with validating (C2 review)", async () => {
  let pending = true;
  const field = createFieldController<{ readonly title: string }, object, string>({
    kind: "text", read: state => state.title, write: async () => undefined, validating: () => pending,
  });
  field.setSource({ state: { title: "" }, client: {} });
  const form = createFormController([field]);
  const order: string[] = [];
  const waiting = form.submit(() => order.push("ran"), { waitForValidation: 2_000 });
  await new Promise(resolve => setTimeout(resolve, 120));
  assert.equal(order.length, 0, "the command waits while the check runs");
  pending = false;
  field.setSource({ state: { title: "" }, client: {} });
  assert.deepEqual(await waiting, { status: "submitted", result: 1 });
  pending = true;
  field.setSource({ state: { title: "" }, client: {} });
  assert.deepEqual(await form.submit(() => order.push("never"), { waitForValidation: 100 }), { status: "validating" });
  assert.deepEqual(await form.submit(() => order.push("default")), { status: "submitted", result: 2 }, "by default it does not wait");
  field.dispose();
});

test("a controller dropped without dispose leaves the page's fields once collected (C2 review)", async () => {
  const gc = (globalThis as { readonly Bun?: { gc(force: boolean): void } }).Bun?.gc;
  if (gc === undefined) return;
  const before = liveFieldCount();
  (() => {
    for (let index = 0; index < 20; index++) createFieldController<{ readonly title: string }, object, string>({ kind: "text", read: state => state.title, write: async () => undefined });
  })();
  assert.ok(liveFieldCount() >= before + 20 || liveFieldCount() >= 20);
  for (let attempt = 0; attempt < 10 && liveFieldCount() > before; attempt++) {
    gc(true);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(liveFieldCount() <= before, `${liveFieldCount()} fields remain of ${before}`);
});

test("field-flush/1: the page writes its pause drafts and answers flush; a keyboard shortcut writes them too (C2 review)", async () => {
  const host = globalThis as unknown as Record<symbol, { dispose?: () => void } | undefined>;
  const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
  host[runtimeKey]?.dispose?.();
  delete host[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("editor", { state: { title: "" } });
  try {
    const editor = await connectView<{ readonly title: string }>({ contract: "Tests.Editor:fp-1", route: "editor", members: { title: bridgeWire.string } });
    const field = createFieldController({ kind: "text", read: (state: { readonly title: string }) => state.title, write: (target: typeof editor, value: string) => target.set("title", value) },
      { commit: "pause", delay: 60_000 });
    editor.subscribe(state => field.setSource({ state, client: editor }));
    field.inputText("Typed before a menu command");
    await settle();
    assert.equal(bridge.calls.some(call => call.kind === "set"), false);
    // Other tests' fields may hold drafts that never settle: the page answers within its limit in any case.
    assert.equal(await bridge.flushFields(4_000), true);
    const kinds = bridge.calls.map(call => call.kind);
    assert.ok(kinds.indexOf("set") >= 0 && kinds.indexOf("set") < kinds.indexOf("flush-reply"), `the draft is written before flush-reply: ${kinds.join(", ")}`);
    assert.equal(field.current.editing, false);

    const page = globalThis as { readonly dispatchEvent?: (event: Event) => boolean };
    if (typeof page.dispatchEvent === "function" && typeof Event === "function") {
      field.inputText("Typed before Ctrl+W");
      const event = new Event("keydown");
      Object.defineProperty(event, "ctrlKey", { value: true });
      page.dispatchEvent(event);
      await settle();
      assert.equal(bridge.calls.filter(call => call.kind === "set").length, 2, "a shortcut writes the draft");
    }
    field.dispose();
    editor.dispose();
  } finally {
    host[runtimeKey]?.dispose?.();
    delete host[runtimeKey];
  }
});
