import * as React from "react";
import { createElement, StrictMode, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import { bridgeSuccess, type BridgeOperation, type BridgeOperationStatus } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { useOperation, useTextField, useView, type OperationHandle, type TextFieldHandle } from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

type State = { readonly title: string };
let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
});

async function mount(element: ReactNode): Promise<void> {
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, element));
  });
}
const change = (value: string) => ({ currentTarget: { value } });

/** A mock View whose setter trims, behind 100 ms of virtual latency. */
async function editor(): Promise<{ bridge: MockBridge; client: ViewConnection<State> }> {
  const bridge = installMockBridge(createMockBridge({ scheduling: "manual", latency: 100 }));
  bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: (value as string).trim() }) } });
  const pending = connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
  for (let round = 0; round < 20; round++) await bridge.advance(50);
  return { bridge, client: await pending };
}

describe("useTextField", () => {
  test("keeps the draft while writes are in flight, coalesces them, then shows the View's normalized value", async () => {
    const { bridge, client } = await editor();
    let field: TextFieldHandle | undefined;
    function Editor(): ReactNode {
      const view = useView(client);
      field = useTextField(view, state => state.title, (connected, value) => connected.set("title", value));
      return createElement("input", { value: field.value, onChange: field.onChange, disabled: view.client === undefined });
    }
    await mount(createElement(Editor));
    expectTypeOf(field!.value).toEqualTypeOf<string>();
    expect(field!).toMatchObject({ value: "", editing: false, error: undefined });
    for (const typed of ["a", "ab", "ab ", "ab c "]) {
      await act(async () => { field!.onChange(change(typed)); });
      expect(field!.value).toBe(typed);
      expect(field!.editing).toBe(true);
      await act(async () => { await bridge.advance(30); });
      expect(field!.value).toBe(typed);
    }
    await act(async () => { for (let round = 0; round < 20; round++) await bridge.advance(50); });
    expect(field!).toMatchObject({ value: "ab c", editing: false, error: undefined });
    expect(document.querySelector("input")!.value).toBe("ab c");
    // One write in flight: "a", then the latest value typed meanwhile.
    expect(bridge.calls.filter(call => call.kind === "set").length).toBe(2);
    client.dispose();
  });

  test("a failed write keeps the draft and sets error; reset shows the View's value", async () => {
    let fail = true;
    const written: string[] = [];
    const client = {
      snapshot: { title: "saved" } as State,
      subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
      dispose() {},
      async write(value: string) { written.push(value); if (fail) throw new Error("rejected"); },
    };
    let field: TextFieldHandle | undefined;
    function Editor(): ReactNode {
      const view = useView(client);
      field = useTextField(view, state => state.title, (connected, value) => connected.write(value), { coalesce: false });
      return createElement("input", { value: field.value, onChange: field.onChange });
    }
    await mount(createElement(Editor));
    expect(field!.value).toBe("saved");
    await act(async () => { field!.onChange(change("saved!")); });
    expect(field!).toMatchObject({ value: "saved!", editing: true });
    expect((field!.error as Error).message).toBe("rejected");
    await act(async () => { field!.reset(); });
    expect(field!).toMatchObject({ value: "saved", editing: false, error: undefined });
    fail = false;
    expect(written).toEqual(["saved!"]);
  });

  test("writes nothing during IME composition and writes the composed text at its end", async () => {
    const written: string[] = [];
    const client = {
      snapshot: { title: "" } as State,
      subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
      dispose() {},
    };
    let field: TextFieldHandle | undefined;
    function Editor(): ReactNode {
      field = useTextField(useView(client), state => state.title, async (_client, value) => { written.push(value); });
      return createElement("input", { value: field.value, onChange: field.onChange });
    }
    await mount(createElement(Editor));
    await act(async () => {
      field!.onCompositionStart();
      field!.onChange(change("k"));
      field!.onChange(change("か"));
    });
    expect(field!.value).toBe("か");
    expect(written).toEqual([]);
    await act(async () => { field!.onCompositionEnd(change("漢")); });
    expect(written).toEqual(["漢"]);
  });
});

describe("useOperation progress", () => {
  test("follows the running operation's progress", async () => {
    const listeners = new Set<(status: BridgeOperationStatus) => void>();
    let finish!: (status: BridgeOperationStatus) => void;
    const terminal = new Promise<BridgeOperationStatus>(resolve => { finish = resolve; });
    const operation: BridgeOperation = {
      requestId: "upload", status: () => terminal, completion: terminal, wait: () => terminal,
      subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
      async cancel() { return { contract: "test", requestId: "upload", kind: "cancellation-requested" }; },
      async outcome() { await terminal; return bridgeSuccess(undefined); },
    };
    const report = (progress: number) => { for (const listener of [...listeners]) listener({ contract: "test", requestId: "upload", kind: "running", progress }); };
    let handle: OperationHandle<[], void> | undefined;
    function Runner(): ReactNode {
      handle = useOperation(() => operation);
      return createElement("progress", { value: handle.progress });
    }
    await mount(createElement(Runner));
    expectTypeOf(handle!.progress).toEqualTypeOf<number | undefined>();
    let run: Promise<unknown> | undefined;
    await act(async () => { run = handle!.run(); });
    expect(handle!.progress).toBeUndefined();
    await act(async () => { report(0.5); });
    expect(handle!.progress).toBe(0.5);
    await act(async () => { finish({ contract: "test", requestId: "upload", kind: "succeeded" }); await run; });
    expect(handle!.progress).toBeUndefined();
    expect(listeners.size).toBe(0);
  });
});
