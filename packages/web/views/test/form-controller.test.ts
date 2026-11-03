// Forms (C2): createFormController's state and submit, and the page's field drafts written before a window close.
import assert from "node:assert/strict";
import { test } from "node:test";
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
  const failed = await form.submit(() => order.push("not run"));
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
