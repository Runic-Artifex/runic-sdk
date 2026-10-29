// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, shallowRef, vModelText, withDirectives, type WritableComputedRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import { bridgeSuccess, type BridgeOperation, type BridgeOperationStatus } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { useOperation, useTextField, useView, type TextFieldHandle } from "../dist/index.js";

type State = { readonly title: string };

/** A mock View whose setter trims, behind 100 ms of virtual latency. */
async function editor(): Promise<{ bridge: MockBridge; client: ViewConnection<State> }> {
  const bridge = installMockBridge(createMockBridge({ scheduling: "manual", latency: 100 }));
  bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: (value as string).trim() }) } });
  const pending = connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
  for (let round = 0; round < 20; round++) await bridge.advance(50);
  return { bridge, client: await pending };
}

function fakeClient(title: string) {
  const client = {
    snapshot: { title } as State,
    subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
    dispose() {},
  };
  return client;
}

describe("useTextField", () => {
  test("v-model keeps the draft while writes are in flight, then shows the View's normalized value", async () => {
    const { bridge, client } = await editor();
    let field: TextFieldHandle | undefined;
    const App = defineComponent({
      setup() {
        const view = useView(client);
        field = useTextField(view, state => state.title, (connected, value) => connected.set("title", value));
        const { value } = field;
        return () => withDirectives(h("input", { "onUpdate:modelValue": (text: string) => { value.value = text; } }), [[vModelText, value.value]]);
      },
    });
    const target = document.createElement("div");
    const app = createApp(App);
    app.mount(target);
    const input = target.querySelector("input")!;
    expectTypeOf(field!.value).toEqualTypeOf<WritableComputedRef<string>>();
    for (const typed of ["a", "ab", "ab ", "ab c "]) {
      input.value = typed;
      input.dispatchEvent(new Event("input"));
      await nextTick();
      expect(field!.value.value).toBe(typed);
      expect(field!.editing.value).toBe(true);
      await bridge.advance(30);
      await nextTick();
      expect(input.value).toBe(typed);
    }
    for (let round = 0; round < 20; round++) await bridge.advance(50);
    await nextTick();
    expect(field!.value.value).toBe("ab c");
    expect(field!.editing.value).toBe(false);
    expect(input.value).toBe("ab c");
    expect(bridge.calls.filter(call => call.kind === "set").length).toBe(2);
    app.unmount();
    client.dispose();
  });

  test("a failed write keeps the draft and sets error; reset shows the View's value", async () => {
    const client = fakeClient("saved");
    const scope = effectScope();
    const field = scope.run(() => useTextField(useView(client), state => state.title, async () => { throw new Error("rejected"); }))!;
    await nextTick();
    expect(field.value.value).toBe("saved");
    field.value.value = "saved!";
    await Promise.resolve();
    await nextTick();
    expect(field.value.value).toBe("saved!");
    expect(field.editing.value).toBe(true);
    expect((field.error.value as Error).message).toBe("rejected");
    field.reset();
    expect(field.value.value).toBe("saved");
    expect(field.editing.value).toBe(false);
    expect(field.error.value).toBeUndefined();
    scope.stop();
  });

  test("follows a changed state and client of the view refs", async () => {
    const view = { state: shallowRef<State | undefined>(undefined), client: shallowRef<ReturnType<typeof fakeClient> | undefined>(undefined) };
    const written: string[] = [];
    const scope = effectScope();
    const field = scope.run(() => useTextField(view, state => state.title, async (_client, value) => { written.push(value); }))!;
    expect(field.value.value).toBe("");
    // Typed before a client is connected: written once one is.
    field.value.value = "early";
    const client = fakeClient("remote");
    view.state.value = client.snapshot;
    view.client.value = client;
    await nextTick();
    expect(written).toEqual(["early"]);
    await Promise.resolve();
    await nextTick();
    expect(field.editing.value).toBe(false);
    expect(field.value.value).toBe("remote");
    view.state.value = { title: "changed" };
    await nextTick();
    expect(field.value.value).toBe("changed");
    scope.stop();
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
    const scope = effectScope();
    const { run, progress } = scope.run(() => useOperation(() => operation))!;
    expectTypeOf(progress.value).toEqualTypeOf<number | undefined>();
    const result = run();
    await Promise.resolve();
    await Promise.resolve();
    for (const listener of [...listeners]) listener({ contract: "test", requestId: "upload", kind: "running", progress: 0.5 });
    expect(progress.value).toBe(0.5);
    finish({ contract: "test", requestId: "upload", kind: "succeeded" });
    await result;
    expect(progress.value).toBeUndefined();
    expect(listeners.size).toBe(0);
    scope.stop();
  });
});
