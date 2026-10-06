// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, shallowRef, type PropType } from "vue";
import { describe, expect, test } from "vitest";
import { useCollectionViewport, useCommand, useView, ViewOutlet, type ViewRegistry } from "../dist/index.js";

type State = { readonly count: number };

function fakeClient(initial: number) {
  let current: State = { count: initial };
  const listeners = new Set<(state: State) => void>();
  return {
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) {
      listener(current);
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    dispose() { listeners.clear(); },
    push(count: number) { current = { count }; for (const listener of [...listeners]) listener(current); },
  };
}
type FakeClient = ReturnType<typeof fakeClient>;

type Page =
  | { readonly kind: "counter"; connect(): Promise<FakeClient> }
  | { readonly kind: "welcome"; connect(): Promise<FakeClient> };

const settle = async () => { await Promise.resolve(); await nextTick(); };

describe("useView glue", () => {
  test("reports pending while a reference connects", async () => {
    let resolve!: (client: FakeClient) => void;
    const scope = effectScope();
    const view = scope.run(() => useView({ connect: () => new Promise<FakeClient>(done => { resolve = done; }) }))!;
    expect(view.pending.value).toBe(true);
    resolve(fakeClient(1));
    await settle();
    expect(view.pending.value).toBe(false);
    expect(view.state.value).toEqual({ count: 1 });
    scope.stop();
  });
});

describe("ViewOutlet", () => {
  const Counter = defineComponent({ props: { page: { type: Object as PropType<Extract<Page, { kind: "counter" }>>, required: true } },
    setup: props => () => h("output", `counter:${props.page.kind}`) });
  const Welcome = defineComponent({ props: { page: { type: Object as PropType<Extract<Page, { kind: "welcome" }>>, required: true } },
    setup: props => () => h("output", `welcome:${props.page.kind}`) });
  const registry = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page>;

  test("renders the registered component, remounts on a new reference and alerts for a missing kind", async () => {
    const content = shallowRef<Page | undefined>();
    const pages = shallowRef<ViewRegistry<Page>>(registry);
    const host = document.createElement("div");
    const app = createApp({ setup: () => () => h(ViewOutlet<Page>, { content: content.value, registry: pages.value }, { default: () => "empty" }) });
    app.mount(host);
    expect(host.textContent).toBe("empty");
    const counter: Page = { kind: "counter", connect: async () => fakeClient(1) };
    content.value = counter;
    await nextTick();
    expect(host.textContent).toBe("counter:counter");
    const first = host.querySelector("output");
    content.value = { kind: "counter", connect: async () => fakeClient(2) };
    await nextTick();
    expect(host.querySelector("output")).not.toBe(first);
    content.value = { kind: "welcome", connect: async () => fakeClient(3) };
    await nextTick();
    expect(host.textContent).toBe("welcome:welcome");
    pages.value = { counter: Counter } as unknown as ViewRegistry<Page>;
    await nextTick();
    expect(host.querySelector("[role=alert]")?.textContent).toBe("No web component is registered for welcome.");
    app.unmount();
  });
});

describe("useCommand", () => {
  test("exposes pending and error reactively and never rejects", async () => {
    const outcomes: (() => void)[] = [];
    const scope = effectScope();
    const command = scope.run(() => useCommand((fail: boolean) => new Promise<number>((resolve, reject) => {
      outcomes.push(() => fail ? reject(new Error("rejected")) : resolve(1));
    })))!;
    const failed = command.run(true);
    expect(command.pending).toBe(true);
    outcomes.shift()!();
    expect(await failed).toBeUndefined();
    expect(command.pending).toBe(false);
    expect((command.error as Error).message).toBe("rejected");
    const succeeded = command.run(false);
    expect(command.error).toBeUndefined();
    outcomes.shift()!();
    expect(await succeeded).toBe(1);
    scope.stop();
  });
});

describe("useCollectionViewport", () => {
  test("measures the attached container and follows the options getter", async () => {
    const count = shallowRef(100);
    const scope = effectScope();
    const list = scope.run(() => useCollectionViewport(() => ({ totalCount: count.value, rowHeight: 20, overscan: 0 })))!;
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 200 });
    list.attach(element);
    expect(list.viewport.value).toEqual({ start: 0, size: 10, offset: 0, totalSize: 2000 });
    count.value = 5;
    await nextTick();
    expect(list.viewport.value).toEqual({ start: 0, size: 5, offset: 0, totalSize: 100 });
    list.attach(null);
    scope.stop();
  });
});
