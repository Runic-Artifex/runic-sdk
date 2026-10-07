// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, shallowRef, watchEffect, type PropType } from "vue";
import { describe, expect, test, expectTypeOf } from "vitest";
import { useCollectionViewport, useCommand, useView, ViewOutlet, type ViewRegistry } from "../dist/index.js";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "@runic-artifex/views";

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

  test("keeps a declared failure apart from error; the latest run wins", async () => {
    type Fail = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
    const outcomes: ((value: BridgeOutcome<number, Fail> | Error) => void)[] = [];
    const command = effectScope().run(() => useCommand(() => new Promise<BridgeOutcome<number, Fail>>((resolve, reject) => {
      outcomes.push(value => value instanceof Error ? reject(value) : resolve(value));
    })))!;
    expectTypeOf(command.failure).toEqualTypeOf<Fail | undefined>();
    // A watcher observes failure like any other reactive property.
    const observed: (Fail | undefined)[] = [];
    const watching = effectScope();
    watching.run(() => watchEffect(() => { observed.push(command.failure); }, { flush: "sync" }));
    const failed = command.run();
    outcomes[0]!(bridgeFailure({ $case: "titleRequired" }));
    await failed;
    expect(observed).toEqual([undefined, { $case: "titleRequired" }]);
    watching.stop();
    expect(command.failure).toEqual({ $case: "titleRequired" });
    expect(command.error).toBeUndefined();
    const stale = command.run();
    const latest = command.run();
    expect(command.failure).toBeUndefined();
    outcomes[2]!(bridgeSuccess(2));
    await latest;
    outcomes[1]!(bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
    await stale;
    expect(command.failure).toBeUndefined();
    const broken = command.run();
    outcomes[3]!(new Error("broken"));
    await broken;
    expect((command.error as Error).message).toBe("broken");
    expect(command.failure).toBeUndefined();
    const again = command.run();
    outcomes[4]!(bridgeFailure({ $case: "titleRequired" }));
    await again;
    command.reset();
    expect(command.failure).toBeUndefined();
    expect(command.error).toBeUndefined();
    const plain = effectScope().run(() => useCommand(() => Promise.resolve(1)))!;
    expectTypeOf(plain.failure).toEqualTypeOf<undefined>();
  });

  test("infers the result of a command that returns one of several command promises", async () => {
    type State = { readonly title: string };
    type SaveFailure = { readonly $case: "titleRequired" };
    type PublishFailure = { readonly $case: "offline" };
    const client = {
      save: () => Promise.resolve<BridgeOutcome<State, SaveFailure>>(bridgeFailure({ $case: "titleRequired" })),
      publish: () => Promise.resolve<BridgeOutcome<State, PublishFailure>>(bridgeSuccess({ title: "Published" })),
      discard: () => Promise.resolve<State>({ title: "Discarded" }),
    };
    const command = effectScope().run(() => useCommand((name: "save" | "publish" | "discard") =>
      name === "save" ? client.save() : name === "publish" ? client.publish() : client.discard()))!;
    expectTypeOf(command.run).returns.resolves.toEqualTypeOf<BridgeOutcome<State, SaveFailure> | BridgeOutcome<State, PublishFailure> | State | undefined>();
    expectTypeOf(command.failure).toEqualTypeOf<SaveFailure | PublishFailure | undefined>();
    await command.run("save");
    expect(command.failure).toEqual({ $case: "titleRequired" });
    expect(await command.run("discard")).toEqual({ title: "Discarded" });
    expect(command.failure).toBeUndefined();
    const optional = effectScope().run(() => useCommand((name: "save" | "discard") => name === "save" ? undefined : client.discard()))!;
    expectTypeOf(optional.failure).toEqualTypeOf<undefined>();
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
