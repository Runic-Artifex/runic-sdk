// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, expectTypeOf, test, vi } from "vitest";
import type { FieldWriteOptions, FieldWriteReceipt } from "@runic-artifex/views";
import { FieldError, useField, type BridgeField, type FieldHandle } from "../src/views/index.js";
import FieldOwner from "./fixtures/FieldOwner.svelte";

type State = { readonly title: string; readonly step: number; readonly done: boolean; readonly titleErrors: readonly string[] };
type Call = { readonly member: string; readonly value: unknown; readonly checked: boolean; resolve(result?: unknown): void };
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); flushSync(); };

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

function form(client: Client, options = {}) {
  const target = document.createElement("div");
  document.body.append(target);
  const owner = mount(FieldOwner<Client>, { target, props: { source: client, fields: Fields, options } });
  flushSync();
  const element = (id: string) => target.querySelector<HTMLInputElement>(`#${id}`)!;
  return {
    owner, title: owner.title as FieldHandle<string>, step: owner.step as FieldHandle<number>, done: owner.done as FieldHandle<boolean>, element,
    destroy() { unmount(owner); target.remove(); },
  };
}
const type = (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event("input")); flushSync(); };

describe("useField", () => {
  test("the attachment writes a text field's input, writes nothing during composition and commits on blur in pause mode", async () => {
    const client = fakeClient({ title: "saved" });
    const { title, element, destroy } = form(client, { commit: "pause", delay: 60_000 });
    expectTypeOf(title.commit).returns.toEqualTypeOf<Promise<void>>();
    const input = element("title");
    expect(input.value).toBe("saved");
    expect(input.getAttribute("aria-invalid")).toBe("false");
    type(input, "saved!");
    expect(title).toMatchObject({ value: "saved!", text: "saved!", editing: true });
    input.dispatchEvent(new Event("compositionstart"));
    type(input, "saved! k");
    input.value = "saved! 漢";
    input.dispatchEvent(new Event("compositionend"));
    flushSync();
    expect(title.value).toBe("saved! 漢");
    expect(client.calls).toEqual([]);
    // bind:value on another input follows the same field.
    expect(element("bound").value).toBe("saved! 漢");
    input.dispatchEvent(new Event("blur"));
    expect(client.calls.map(call => call.value)).toEqual(["saved! 漢"]);
    let committed = false;
    void title.commit().then(() => { committed = true; });
    await settle();
    expect(committed).toBe(false);
    client.calls[0]!.resolve();
    await settle();
    expect(committed).toBe(true);
    expect(title).toMatchObject({ value: "saved! 漢", editing: false });
    destroy();
  });

  test("the attachment keeps the element's text without setting it while it already shows it", async () => {
    const client = fakeClient({ title: "a" });
    const { title, element, destroy } = form(client);
    const input = element("title");
    const assigned: string[] = [];
    const added = vi.spyOn(input, "addEventListener");
    const property = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), "value")!;
    Object.defineProperty(input, "value", {
      configurable: true, get() { return property.get!.call(this); }, set(value: string) { assigned.push(value); property.set!.call(this, value); },
    });
    property.set!.call(input, "ab");
    input.dispatchEvent(new Event("input"));
    flushSync();
    expect(title.value).toBe("ab");
    client.calls[0]!.resolve();
    await settle();
    expect(title.editing).toBe(false);
    expect(assigned).toEqual([]);
    // Another writer changes the field: the element shows it.
    client.apply("title", "changed");
    flushSync();
    expect(assigned).toEqual(["changed"]);
    // A change of the field does not wire the element again.
    expect(added).not.toHaveBeenCalled();
    // bind:value writes through the handle too.
    const bound = element("bound");
    type(bound, "typed there");
    expect(input.value).toBe("typed there");
    destroy();
  });

  test("a number field keeps `-` and `1.` as text, keeps invalid text as the draft and writes numbers", async () => {
    const client = fakeClient({ step: 3 });
    const { step, element, destroy } = form(client);
    const input = element("step");
    expect(input.value).toBe("3");
    type(input, "-");
    expect(step).toMatchObject({ text: "-", editing: true });
    expect((step.error as FieldError).kind).toBe("invalid-text");
    expect(input.getAttribute("aria-invalid")).toBe("true");
    expect(input.value).toBe("-");
    type(input, "1e");
    expect(client.calls).toEqual([]);
    type(input, "1.");
    expect(step).toMatchObject({ value: 1, text: "1.", error: undefined });
    expect(client.calls.map(call => call.value)).toEqual([1]);
    client.calls[0]!.resolve();
    await settle();
    expect(step).toMatchObject({ value: 1, text: "1.", editing: false });
    expect(input.value).toBe("1.");
    type(input, "1.5");
    expect(client.calls.map(call => call.value)).toEqual([1, 1.5]);
    expect(typeof client.calls[1]!.value).toBe("number");
    destroy();
  });

  test("a boolean field binds a checkbox", async () => {
    const client = fakeClient({ done: false });
    const { done, element, destroy } = form(client);
    const input = element("done");
    expect(input.checked).toBe(false);
    input.checked = true;
    input.dispatchEvent(new Event("change"));
    flushSync();
    expect(client.calls.map(call => call.value)).toEqual([true]);
    expect(done.value).toBe(true);
    client.calls[0]!.resolve();
    await settle();
    expect(done).toMatchObject({ value: true, editing: false });
    client.apply("done", false);
    flushSync();
    expect(input.checked).toBe(false);
    destroy();
  });

  test("a checked write that is rejected keeps the draft and shows the reason; a conflict waits for retry", async () => {
    const client = fakeClient({ title: "saved" });
    const { title, element, destroy } = form(client, { checked: true });
    type(element("title"), "Bad");
    expect(client.calls[0]).toMatchObject({ value: "Bad", checked: true });
    client.calls[0]!.resolve({ kind: "rejected", message: "Titles cannot be Bad." });
    await settle();
    expect(title).toMatchObject({ value: "Bad", editing: true });
    expect(title.error).toBeInstanceOf(FieldError);
    expect((title.error as FieldError).kind).toBe("rejected");
    expect((title.error as FieldError).message).toBe("Titles cannot be Bad.");
    expect(element("title").value).toBe("Bad");
    expect(element("title").getAttribute("aria-invalid")).toBe("true");
    title.reset();
    flushSync();
    expect(title).toMatchObject({ value: "saved", editing: false, error: undefined });
    expect(element("title").value).toBe("saved");
    title.input("mine");
    client.calls[1]!.resolve({ kind: "conflict", incoming: { value: "theirs", version: 7 }, message: "Changed." });
    await settle();
    expect(title).toMatchObject({ value: "mine", editing: true, conflict: { value: "theirs", version: 7 } });
    title.retry();
    expect(client.calls.map(call => call.value)).toEqual(["Bad", "mine", "mine"]);
    expect(title.conflict).toBeUndefined();
    destroy();
  });

  test("errors come from the descriptor and set aria-invalid", () => {
    const client = fakeClient({ titleErrors: ["A title is required."] });
    const { title, element, destroy } = form(client);
    expect(title.errors).toEqual(["A title is required."]);
    expect(element("title").getAttribute("aria-invalid")).toBe("true");
    client.apply("titleErrors", []);
    flushSync();
    expect(title.errors).toEqual([]);
    expect(element("title").getAttribute("aria-invalid")).toBe("false");
    destroy();
  });

  test("works outside a component under $effect.root", async () => {
    const client = fakeClient({ title: "x" });
    let field!: FieldHandle<string>;
    const destroy = $effect.root(() => { field = useField(() => ({ state: client.snapshot, client }), Fields.title); });
    flushSync();
    expectTypeOf(field.value).toEqualTypeOf<string>();
    expect(field.value).toBe("x");
    field.text = "y";
    expect(client.calls.map(call => call.value)).toEqual(["y"]);
    destroy();
  });
});
