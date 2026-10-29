// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, expectTypeOf, test } from "vitest";
import { bridgeSuccess, type BridgeOperation, type BridgeOperationStatus, type ViewClient } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { useOperation, useTextField, type TextFieldHandle } from "../src/views/index.js";
import TextFieldOwner from "./fixtures/TextFieldOwner.svelte";

type State = { readonly title: string };
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); flushSync(); };

/** A mock View whose setter trims, behind 100 ms of virtual latency. */
async function editor(): Promise<{ bridge: MockBridge; client: ViewConnection<State> }> {
  const bridge = installMockBridge(createMockBridge({ scheduling: "manual", latency: 100 }));
  bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: (value as string).trim() }) } });
  const pending = connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
  for (let round = 0; round < 20; round++) await bridge.advance(50);
  return { bridge, client: await pending };
}

function fakeClient(title: string): ViewClient<State> {
  const client = {
    snapshot: { title } as State,
    subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
    dispose() {},
  };
  return client;
}

describe("useTextField", () => {
  test("bind:value keeps the draft while writes are in flight, then shows the View's normalized value", async () => {
    const { bridge, client } = await editor();
    const target = document.createElement("div");
    const owner = mount(TextFieldOwner, { target, props: { source: client } });
    flushSync();
    const field: TextFieldHandle = owner.title;
    const input = target.querySelector("input")!;
    for (const typed of ["a", "ab", "ab ", "ab c "]) {
      input.value = typed;
      input.dispatchEvent(new Event("input"));
      flushSync();
      expect(field.value).toBe(typed);
      expect(field.editing).toBe(true);
      await bridge.advance(30);
      flushSync();
      expect(input.value).toBe(typed);
    }
    for (let round = 0; round < 20; round++) await bridge.advance(50);
    flushSync();
    expect(field.value).toBe("ab c");
    expect(field.editing).toBe(false);
    expect(input.value).toBe("ab c");
    // One write in flight: "a", then the latest value typed meanwhile.
    expect(bridge.calls.filter(call => call.kind === "set").length).toBe(2);
    unmount(owner);
    client.dispose();
  });

  test("a failed write keeps the draft and sets error; reset shows the View's value", async () => {
    const client = fakeClient("saved");
    let field!: TextFieldHandle;
    const destroy = $effect.root(() => {
      field = useTextField(() => ({ state: client.snapshot, client }), state => state.title, async () => { throw new Error("rejected"); });
    });
    flushSync();
    expectTypeOf(field.value).toEqualTypeOf<string>();
    expect(field.value).toBe("saved");
    field.value = "saved!";
    await settle();
    expect(field.value).toBe("saved!");
    expect(field.editing).toBe(true);
    expect((field.error as Error).message).toBe("rejected");
    field.reset();
    flushSync();
    expect(field.value).toBe("saved");
    expect(field.editing).toBe(false);
    expect(field.error).toBeUndefined();
    destroy();
  });

  test("follows the view's state and writes nothing during IME composition", async () => {
    let view = $state<{ state: State | undefined; client: ViewClient<State> | undefined }>({ state: undefined, client: undefined });
    const written: string[] = [];
    let field!: TextFieldHandle;
    const destroy = $effect.root(() => {
      field = useTextField(() => view, state => state.title, async (_client, value) => { written.push(value); });
    });
    flushSync();
    expect(field.value).toBe("");
    const client = fakeClient("remote");
    view = { state: client.snapshot, client };
    flushSync();
    expect(field.value).toBe("remote");
    field.compositionStart();
    field.value = "k";
    field.value = "か";
    flushSync();
    expect(field.value).toBe("か");
    expect(written).toEqual([]);
    field.compositionEnd("漢");
    expect(written).toEqual(["漢"]);
    await settle();
    expect(field.editing).toBe(false);
    destroy();
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
    let handle!: ReturnType<typeof useOperation<[], void>>;
    const seen: (number | undefined)[] = [];
    const destroy = $effect.root(() => {
      handle = useOperation(() => operation);
      $effect(() => { seen.push(handle.progress); });
    });
    flushSync();
    expectTypeOf(handle.progress).toEqualTypeOf<number | undefined>();
    const result = handle.run();
    await settle();
    for (const listener of [...listeners]) listener({ contract: "test", requestId: "upload", kind: "running", progress: 0.5 });
    flushSync();
    expect(handle.progress).toBe(0.5);
    finish({ contract: "test", requestId: "upload", kind: "succeeded" });
    await result;
    flushSync();
    expect(handle.progress).toBeUndefined();
    expect(seen.filter((value, index) => index === 0 || value !== seen[index - 1])).toEqual([undefined, 0.5, undefined]);
    expect(listeners.size).toBe(0);
    destroy();
  });
});
