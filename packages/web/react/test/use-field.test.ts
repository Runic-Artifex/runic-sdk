import * as React from "react";
import { createElement, StrictMode, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import type { FieldWriteOptions, FieldWriteReceipt } from "@runic-artifex/views";
import {
  FieldError,
  useField,
  useView,
  type BridgeField,
  type FieldCheckboxProps,
  type FieldHandle,
  type FieldTextProps,
} from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

type State = { readonly title: string; readonly step: number; readonly done: boolean; readonly titleErrors: readonly string[] };
type Call = { readonly member: string; readonly value: unknown; readonly checked: boolean; resolve(result?: unknown): void };

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
  step: descriptor<"step", number>("step", "number", { integer: false }),
  done: descriptor<"done", boolean>("done", "boolean"),
};

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  document.body.innerHTML = "";
});

async function mount(element: ReactNode): Promise<void> {
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, element));
  });
}
const change = (value: string) => ({ currentTarget: { value } });
const settle = () => act(async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); });

describe("useField", () => {
  test("a text field in pause mode keeps typing and composition as the draft and writes on blur", async () => {
    const client = fakeClient({ title: "saved" });
    let field: FieldHandle<string> | undefined;
    const handlers: unknown[] = [];
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.title, { commit: "pause", delay: 60_000 });
      handlers.push(field.inputProps.onChange);
      return createElement("input", { id: "title", ...field.inputProps });
    }
    await mount(createElement(Editor));
    // StrictMode's second mount creates the field again; from then on the handlers are the same on every render.
    handlers.length = 0;
    expectTypeOf(field!.inputProps).toEqualTypeOf<FieldTextProps>();
    expectTypeOf(field!.commit).returns.toEqualTypeOf<Promise<void>>();
    const input = document.querySelector<HTMLInputElement>("#title")!;
    expect(field!).toMatchObject({ value: "saved", text: "saved", editing: false, error: undefined, errors: [] });
    expect(input.value).toBe("saved");
    expect(input.getAttribute("aria-invalid")).toBe("false");
    await act(async () => { field!.inputProps.onChange(change("saved!")); });
    expect(field!).toMatchObject({ value: "saved!", editing: true });
    expect(input.value).toBe("saved!");
    await act(async () => {
      field!.inputProps.onCompositionStart();
      field!.inputProps.onChange(change("saved! k"));
      field!.inputProps.onCompositionEnd(change("saved! 漢"));
    });
    expect(field!.value).toBe("saved! 漢");
    expect(client.calls).toEqual([]);
    let committed = false;
    await act(async () => { void field!.commit().then(() => { committed = true; }); });
    expect(client.calls.map(call => call.value)).toEqual(["saved! 漢"]);
    await settle();
    expect(committed).toBe(false);
    await act(async () => { client.calls[0]!.resolve(); });
    await settle();
    expect(committed).toBe(true);
    expect(field!).toMatchObject({ value: "saved! 漢", editing: false });
    expect(handlers.length).toBeGreaterThan(3);
    expect(new Set(handlers).size).toBe(1);
  });

  test("blur commits through inputProps.onBlur", async () => {
    const client = fakeClient();
    let field: FieldHandle<string> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.title, { commit: "pause", delay: 60_000 });
      return createElement("input", field.inputProps);
    }
    await mount(createElement(Editor));
    await act(async () => { field!.inputProps.onChange(change("typed")); });
    expect(client.calls).toEqual([]);
    await act(async () => { field!.inputProps.onBlur(); });
    expect(client.calls.map(call => call.value)).toEqual(["typed"]);
  });

  test("a number field keeps `-` and `1.` as text, keeps invalid text as the draft and writes numbers", async () => {
    const client = fakeClient({ step: 3 });
    let field: FieldHandle<number> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.step);
      return createElement("input", { id: "step", ...field.inputProps });
    }
    await mount(createElement(Editor));
    const input = document.querySelector<HTMLInputElement>("#step")!;
    expect(field!).toMatchObject({ value: 3, text: "3" });
    await act(async () => { field!.inputProps.onChange(change("-")); });
    expect(field!).toMatchObject({ text: "-", editing: true });
    expect((field!.error as FieldError).kind).toBe("invalid-text");
    expect(field!.inputProps["aria-invalid"]).toBe(true);
    expect(input.value).toBe("-");
    expect(client.calls).toEqual([]);
    await act(async () => { field!.inputProps.onChange(change("1e")); });
    expect(field!).toMatchObject({ text: "1e", editing: true });
    expect(client.calls).toEqual([]);
    await act(async () => { field!.inputProps.onChange(change("1.")); });
    expect(field!).toMatchObject({ value: 1, text: "1.", error: undefined });
    expect(client.calls.map(call => call.value)).toEqual([1]);
    await act(async () => { client.calls[0]!.resolve(); });
    await settle();
    // The write settled: the text that still means the value stays.
    expect(field!).toMatchObject({ value: 1, text: "1.", editing: false });
    expect(input.value).toBe("1.");
    await act(async () => { field!.inputProps.onChange(change("1.5")); });
    expect(client.calls.map(call => call.value)).toEqual([1, 1.5]);
    expect(typeof client.calls[1]!.value).toBe("number");
  });

  test("a boolean field binds a checkbox", async () => {
    const client = fakeClient({ done: false });
    let field: FieldHandle<boolean> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.done);
      return createElement("input", { id: "done", type: "checkbox", ...field.inputProps });
    }
    await mount(createElement(Editor));
    expectTypeOf(field!.inputProps).toEqualTypeOf<FieldCheckboxProps>();
    const input = document.querySelector<HTMLInputElement>("#done")!;
    expect(input.checked).toBe(false);
    await act(async () => { input.click(); });
    expect(client.calls.map(call => call.value)).toEqual([true]);
    expect(field!.value).toBe(true);
    expect(input.checked).toBe(true);
    await act(async () => { client.calls[0]!.resolve(); });
    await settle();
    expect(field!).toMatchObject({ value: true, editing: false });
    expect(input.checked).toBe(true);
  });

  test("a checked write that is rejected keeps the draft and shows the reason; reset shows the View's value", async () => {
    const client = fakeClient({ title: "saved" });
    let field: FieldHandle<string> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.title, { checked: true });
      return createElement("input", { id: "title", ...field.inputProps });
    }
    await mount(createElement(Editor));
    await act(async () => { field!.inputProps.onChange(change("Bad")); });
    expect(client.calls[0]).toMatchObject({ value: "Bad", checked: true });
    await act(async () => { client.calls[0]!.resolve({ kind: "rejected", message: "Titles cannot be Bad." }); });
    await settle();
    expect(field!).toMatchObject({ value: "Bad", editing: true });
    expect(field!.error).toBeInstanceOf(FieldError);
    expect((field!.error as FieldError).kind).toBe("rejected");
    expect((field!.error as FieldError).message).toBe("Titles cannot be Bad.");
    expect(document.querySelector("#title")!.getAttribute("aria-invalid")).toBe("true");
    await act(async () => { field!.reset(); });
    expect(field!).toMatchObject({ value: "saved", editing: false, error: undefined });
  });

  test("a conflict holds the draft until retry writes it over the producer's value", async () => {
    const client = fakeClient({ title: "saved" });
    let field: FieldHandle<string> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.title, { checked: true });
      return createElement("input", field.inputProps);
    }
    await mount(createElement(Editor));
    await act(async () => { field!.input("mine"); });
    await act(async () => { client.calls[0]!.resolve({ kind: "conflict", incoming: { value: "theirs", version: 7 }, message: "Changed." }); });
    await settle();
    expect(field!).toMatchObject({ value: "mine", editing: true, conflict: { value: "theirs", version: 7 } });
    await act(async () => { field!.retry(); });
    expect(client.calls.map(call => call.value)).toEqual(["mine", "mine"]);
    expect(field!.conflict).toBeUndefined();
  });

  test("errors come from the descriptor and set aria-invalid", async () => {
    const client = fakeClient({ title: "", titleErrors: ["A title is required."] });
    let field: FieldHandle<string> | undefined;
    function Editor(): ReactNode {
      field = useField(useView(client), Fields.title);
      return createElement("input", { id: "title", ...field.inputProps });
    }
    await mount(createElement(Editor));
    expect(field!.errors).toEqual(["A title is required."]);
    expect(field!.error).toBeUndefined();
    expect(document.querySelector("#title")!.getAttribute("aria-invalid")).toBe("true");
    await act(async () => { client.apply("titleErrors", []); });
    expect(field!.errors).toEqual([]);
    expect(document.querySelector("#title")!.getAttribute("aria-invalid")).toBe("false");
  });

  test("renders the state being rendered without a one-render lag", async () => {
    const client = fakeClient({ title: "first" });
    const seen: [string, string][] = [];
    function Editor({ state }: { readonly state: State }): ReactNode {
      const field = useField({ state, client }, Fields.title);
      seen.push([state.title, field.value]);
      return createElement("input", field.inputProps);
    }
    await mount(createElement(Editor, { state: client.snapshot }));
    await act(async () => { root!.render(createElement(StrictMode, null, createElement(Editor, { state: { ...client.snapshot, title: "second" } }))); });
    expect(seen.some(([rendered]) => rendered === "second")).toBe(true);
    for (const [rendered, shown] of seen) expect(shown).toBe(rendered);
  });
});
