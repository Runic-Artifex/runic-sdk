// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, vModelCheckbox, vModelText, withDirectives } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { FieldWriteOptions, FieldWriteReceipt } from "@runic-artifex/views";
import { FieldError, useField, useView, type BridgeField, type FieldHandle } from "../dist/index.js";

type State = { readonly title: string; readonly step: number; readonly done: boolean; readonly titleErrors: readonly string[] };
type Call = { readonly member: string; readonly value: unknown; readonly checked: boolean; resolve(result?: unknown): void };
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); await nextTick(); };

/** A client whose writes wait until the test resolves them; resolving applies the value to the snapshot. */
function fakeClient(initial: Partial<State> = {}) {
  const listeners = new Set<(state: State) => void>();
  const calls: Call[] = [];
  let version = 1;
  const client = {
    snapshot: { title: "", step: 0, done: false, titleErrors: [], ...initial } as State,
    subscribe(listener: (state: State) => void) { listeners.add(listener); listener(client.snapshot); return () => { listeners.delete(listener); }; },
    dispose() {},
    calls,
    apply(member: string, value: unknown) {
      client.snapshot = { ...client.snapshot, [member]: value };
      version++;
      for (const listener of [...listeners]) listener(client.snapshot);
    },
    write(member: keyof State, value: unknown, checked: boolean): Promise<unknown> {
      return new Promise(resolve => calls.push({
        member, value, checked,
        resolve(result?: unknown) {
          if (result === undefined || (result as { kind?: string }).kind === "applied") client.apply(member, value);
          resolve(result ?? client.snapshot);
        },
      }));
    },
    baseline(member: keyof State) { return { value: client.snapshot[member], version }; },
  };
  return client;
}
type Client = ReturnType<typeof fakeClient>;

function descriptor<K extends "title" | "step" | "done", TValue extends State[K]>(
  name: K, kind: BridgeField<Client, State, TValue>["kind"], extra: Partial<BridgeField<Client, State, TValue>> = {},
): BridgeField<Client, State, TValue> {
  return {
    name, kind,
    read: state => state[name] as TValue,
    write: (client, value) => client.write(name, value, false),
    checked: (client, value, _options: FieldWriteOptions<TValue>) => client.write(name, value, true) as Promise<FieldWriteReceipt<TValue>>,
    baseline: client => client.baseline(name) as { value: TValue; version: number },
    ...extra,
  };
}
const Fields = {
  title: descriptor<"title", string>("title", "text", { errors: state => state.titleErrors }),
  step: descriptor<"step", number>("step", "number"),
  done: descriptor<"done", boolean>("done", "boolean"),
};

/** Mounts a component whose render function is what a template with `v-model` on the handle compiles to. */
function mountField<TValue>(create: () => FieldHandle<TValue>, render: (field: FieldHandle<TValue>) => ReturnType<typeof h>) {
  let field!: FieldHandle<TValue>;
  const app = createApp(defineComponent({ setup() { field = create(); return () => render(field); } }));
  const target = document.createElement("div");
  app.mount(target);
  return { field, target, app };
}
const type = (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event("input")); };

describe("useField", () => {
  test("v-model on the reactive handle writes a text field through its setter; pause mode writes on blur", async () => {
    const client = fakeClient({ title: "saved" });
    // <input v-model="title.value" @blur="title.commit" @compositionstart… (v-model handles composition)>
    const { field, target, app } = mountField(() => useField(useView(client), Fields.title, { commit: "pause", delay: 60_000 }),
      title => withDirectives(h("input", {
        "onUpdate:modelValue": ($event: string) => { title.value = $event; }, onBlur: title.commit,
      }), [[vModelText, title.value]]));
    expectTypeOf(field.value).toEqualTypeOf<string>();
    expectTypeOf(field.commit).returns.toEqualTypeOf<Promise<void>>();
    const input = target.querySelector("input")!;
    expect(input.value).toBe("saved");
    type(input, "saved!");
    await nextTick();
    expect(field).toMatchObject({ value: "saved!", text: "saved!", editing: true });
    // v-model waits for an IME composition to end.
    input.dispatchEvent(new Event("compositionstart"));
    type(input, "saved! k");
    await nextTick();
    expect(field.value).toBe("saved!");
    input.value = "saved! 漢";
    input.dispatchEvent(new Event("compositionend"));
    await nextTick();
    expect(field.value).toBe("saved! 漢");
    expect(client.calls).toEqual([]);
    input.dispatchEvent(new Event("blur"));
    expect(client.calls.map(call => call.value)).toEqual(["saved! 漢"]);
    let committed = false;
    void field.commit().then(() => { committed = true; });
    await settle();
    expect(committed).toBe(false);
    client.calls[0]!.resolve();
    await settle();
    expect(committed).toBe(true);
    expect(field).toMatchObject({ value: "saved! 漢", editing: false });
    app.unmount();
  });

  test("a number field bound with v-model on text keeps `-` and `1.` and writes numbers", async () => {
    const client = fakeClient({ step: 3 });
    // <input type="number" v-model="step.text" @blur="step.commit">
    const { field, target, app } = mountField(() => useField(useView(client), Fields.step),
      step => withDirectives(h("input", { "onUpdate:modelValue": ($event: string) => { step.text = $event; } }), [[vModelText, step.text]]));
    const input = target.querySelector("input")!;
    expect(input.value).toBe("3");
    type(input, "-");
    await nextTick();
    expect(field).toMatchObject({ text: "-", editing: true });
    expect((field.error as FieldError).kind).toBe("invalid-text");
    expect(input.value).toBe("-");
    type(input, "1e");
    await nextTick();
    expect(client.calls).toEqual([]);
    type(input, "1.");
    await nextTick();
    expect(field).toMatchObject({ value: 1, text: "1.", error: undefined });
    expect(client.calls.map(call => call.value)).toEqual([1]);
    client.calls[0]!.resolve();
    await settle();
    expect(field).toMatchObject({ value: 1, text: "1.", editing: false });
    expect(input.value).toBe("1.");
    // A string set on value, as `v-model` on `<input type="number">` does while its text is not a number, is text.
    field.value = "1.5" as unknown as number;
    expect(client.calls.map(call => call.value)).toEqual([1, 1.5]);
    expect(typeof client.calls[1]!.value).toBe("number");
    app.unmount();
  });

  test("a boolean field binds a checkbox with v-model", async () => {
    const client = fakeClient({ done: false });
    // <input type="checkbox" v-model="done.value">
    const { field, target, app } = mountField(() => useField(useView(client), Fields.done),
      done => withDirectives(h("input", { type: "checkbox", "onUpdate:modelValue": ($event: boolean) => { done.value = $event; } }),
        [[vModelCheckbox, done.value]]));
    const input = target.querySelector("input")!;
    expect(input.checked).toBe(false);
    input.checked = true;
    input.dispatchEvent(new Event("change"));
    await nextTick();
    expect(client.calls.map(call => call.value)).toEqual([true]);
    expect(field.value).toBe(true);
    client.calls[0]!.resolve();
    await settle();
    expect(field).toMatchObject({ value: true, editing: false });
    expect(input.checked).toBe(true);
    app.unmount();
  });

  test("a checked write that is rejected keeps the draft and shows the reason; a conflict waits for retry", async () => {
    const client = fakeClient({ title: "saved" });
    const scope = effectScope();
    const field = scope.run(() => useField(useView(client), Fields.title, { checked: true }))!;
    await nextTick();
    field.value = "Bad";
    expect(client.calls[0]).toMatchObject({ value: "Bad", checked: true });
    client.calls[0]!.resolve({ kind: "rejected", message: "Titles cannot be Bad." });
    await settle();
    expect(field).toMatchObject({ value: "Bad", editing: true });
    expect(field.error).toBeInstanceOf(FieldError);
    expect((field.error as FieldError).kind).toBe("rejected");
    expect((field.error as FieldError).message).toBe("Titles cannot be Bad.");
    field.reset();
    expect(field).toMatchObject({ value: "saved", editing: false, error: undefined });
    field.input("mine");
    client.calls[1]!.resolve({ kind: "conflict", incoming: { value: "theirs", version: 7 }, message: "Changed." });
    await settle();
    expect(field).toMatchObject({ value: "mine", editing: true, conflict: { value: "theirs", version: 7 } });
    field.retry();
    expect(client.calls.map(call => call.value)).toEqual(["Bad", "mine", "mine"]);
    expect(field.conflict).toBeUndefined();
    scope.stop();
  });

  test("errors come from the descriptor", async () => {
    const client = fakeClient({ titleErrors: ["A title is required."] });
    const scope = effectScope();
    const field = scope.run(() => useField(useView(client), Fields.title))!;
    await nextTick();
    expect(field.errors).toEqual(["A title is required."]);
    client.apply("titleErrors", []);
    await nextTick();
    expect(field.errors).toEqual([]);
    scope.stop();
  });

  test("the handle's read-only members cannot be set", async () => {
    const client = fakeClient();
    const scope = effectScope();
    const field = scope.run(() => useField(useView(client), Fields.title))!;
    const warn = console.warn;
    console.warn = () => {};
    try { (field as { editing: boolean }).editing = true; } catch { /* strict mode */ } finally { console.warn = warn; }
    expect(field.editing).toBe(false);
    scope.stop();
  });
});
