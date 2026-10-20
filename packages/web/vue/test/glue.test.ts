// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, shallowRef, watchEffect, type PropType } from "vue";
import { afterEach, describe, expect, test, expectTypeOf, vi } from "vitest";
import { useCommand, useOperation, useView, ViewOutlet, type ViewRegistry } from "../dist/index.js";
import { useCollectionViewport } from "../dist/experimental.js";
import * as root from "../dist/index.js";
import { BridgeError, bridgeFailure, bridgeSuccess, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome } from "@runic-artifex/views";

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
    expect(command.pending.value).toBe(true);
    outcomes.shift()!();
    expect(await failed).toBeUndefined();
    expect(command.pending.value).toBe(false);
    expect((command.error.value as Error).message).toBe("rejected");
    const succeeded = command.run(false);
    expect(command.error.value).toBeUndefined();
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
    expectTypeOf(command.failure.value).toEqualTypeOf<Fail | undefined>();
    // A watcher observes failure like any other reactive property.
    const observed: (Fail | undefined)[] = [];
    const watching = effectScope();
    watching.run(() => watchEffect(() => { observed.push(command.failure.value); }, { flush: "sync" }));
    const failed = command.run();
    outcomes[0]!(bridgeFailure({ $case: "titleRequired" }));
    await failed;
    expect(observed).toEqual([undefined, { $case: "titleRequired" }]);
    watching.stop();
    expect(command.failure.value).toEqual({ $case: "titleRequired" });
    expect(command.error.value).toBeUndefined();
    const stale = command.run();
    const latest = command.run();
    expect(command.failure.value).toBeUndefined();
    outcomes[2]!(bridgeSuccess(2));
    await latest;
    outcomes[1]!(bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
    await stale;
    expect(command.failure.value).toBeUndefined();
    const broken = command.run();
    outcomes[3]!(new Error("broken"));
    await broken;
    expect((command.error.value as Error).message).toBe("broken");
    expect(command.failure.value).toBeUndefined();
    const again = command.run();
    outcomes[4]!(bridgeFailure({ $case: "titleRequired" }));
    await again;
    command.reset();
    expect(command.failure.value).toBeUndefined();
    expect(command.error.value).toBeUndefined();
    // D40 c: the handle is destructurable, as useView's is.
    const { run, pending } = effectScope().run(() => useCommand(() => Promise.resolve(2)))!;
    const done = run();
    expect(pending.value).toBe(true);
    expect(await done).toBe(2);
    expect(pending.value).toBe(false);
    const plain = effectScope().run(() => useCommand(() => Promise.resolve(1)))!;
    expectTypeOf(plain.failure.value).toEqualTypeOf<undefined>();
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
    expectTypeOf(command.failure.value).toEqualTypeOf<SaveFailure | PublishFailure | undefined>();
    await command.run("save");
    expect(command.failure.value).toEqual({ $case: "titleRequired" });
    expect(await command.run("discard")).toEqual({ title: "Discarded" });
    expect(command.failure.value).toBeUndefined();
    const optional = effectScope().run(() => useCommand((name: "save" | "discard") => name === "save" ? undefined : client.discard()))!;
    expectTypeOf(optional.failure.value).toEqualTypeOf<undefined>();
  });
});

describe("useCollectionViewport", () => {
  test("is only in @runic-artifex/vue/experimental", () => {
    expect("useCollectionViewport" in root).toBe(false);
  });

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

  test("detaches and warns once when attach receives a component instance instead of a native element", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const scope = effectScope();
    const list = scope.run(() => useCollectionViewport(() => ({ totalCount: 100, rowHeight: 20, overscan: 0 })))!;
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 200 });
    list.attach(element);
    expect(list.viewport.value.size).toBe(10);
    const Child = defineComponent({ render: () => h("div") });
    const host = document.createElement("div");
    const app = createApp(Child);
    const instance = app.mount(host);
    list.attach(instance);
    list.attach(instance);
    expect(list.viewport.value.size).toBe(0);
    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0]![0])).toContain("native scroll container");
    list.attach(null);
    expect(warn).toHaveBeenCalledTimes(1);
    app.unmount();
    scope.stop();
    warn.mockRestore();
  });
});

describe("effect scope", () => {
  afterEach(() => { vi.restoreAllMocks(); });

  test("each composable warns in development when no effect scope can release it", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    useView({ connect: () => new Promise<FakeClient>(() => {}) });
    useCommand(() => 1);
    useOperation(() => undefined);
    useCollectionViewport({ totalCount: 0, rowHeight: 20 });
    expect(warn.mock.calls.map(call => String(call[0]).split(" ")[0])).toEqual(["useView", "useCommand", "useOperation", "useCollectionViewport"]);
    expect(String(warn.mock.calls[0]![0])).toContain("outside an active effect scope");
  });

  test("composables inside an effect scope or a component do not warn", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const scope = effectScope();
    scope.run(() => {
      useView({ connect: () => new Promise<FakeClient>(() => {}) });
      useCommand(() => 1);
      useOperation(() => undefined);
      useCollectionViewport({ totalCount: 0, rowHeight: 20 });
    });
    scope.stop();
    const app = createApp(defineComponent({ setup() { useCommand(() => 1); return () => h("div"); } }));
    app.mount(document.createElement("div"));
    await nextTick();
    app.unmount();
    expect(warn).not.toHaveBeenCalled();
  });
});

function fakeOperation(requestId: string) {
  let finish!: (status: BridgeOperationStatus<number>) => void;
  const terminal = new Promise<BridgeOperationStatus<number>>(resolve => { finish = resolve; });
  let cancels = 0;
  const operation: BridgeOperation<number> = {
    requestId,
    status: () => terminal,
    completion: terminal,
    wait: () => terminal,
    async cancel() { cancels++; return { contract: "test", requestId, kind: "cancellation-requested" }; },
    async outcome() {
      const status = await terminal;
      if (status.kind === "succeeded") return bridgeSuccess(status.result as number);
      throw new BridgeError(status.kind === "cancelled" ? "cancelled" : status.kind === "timed-out" ? "timeout" : "failed", status.kind);
    },
  };
  return { operation, finish, cancels: () => cancels };
}

describe("useView options", () => {
  test("release replaces dispose for a client the composable connected", async () => {
    const released: unknown[] = [];
    let disposed = 0;
    const client = { snapshot: { count: 1 }, subscribe: (listener: (state: { count: number }) => void) => { listener({ count: 1 }); return () => {}; },
      dispose: () => { disposed++; } };
    const scope = effectScope();
    scope.run(() => useView({ connect: async () => client }, { release: connected => released.push(connected) }));
    await settle();
    scope.stop();
    expect(released).toEqual([client]);
    expect(disposed).toBe(0);
  });
});

describe("useOperation", () => {
  test("Cancel ends in a cancelled state without an error; failed and timed-out runs are errors", async () => {
    let current = fakeOperation("first");
    const scope = effectScope();
    const seen: boolean[] = [];
    const operation = scope.run(() => {
      const handle = useOperation(() => current.operation);
      watchEffect(() => { seen.push(handle.cancelled.value); });
      return handle;
    })!;
    expectTypeOf(operation.cancelled.value).toEqualTypeOf<boolean>();
    const result = operation.run();
    expect(operation.pending.value).toBe(true);
    await settle();
    await operation.cancel();
    expect(current.cancels()).toBe(1);
    expect(operation.cancellationRequested.value).toBe(true);
    current.finish({ contract: "test", requestId: "first", kind: "cancelled" });
    expect(await result).toBeUndefined();
    await nextTick();
    expect(operation.cancelled.value).toBe(true);
    expect(operation.error.value).toBeUndefined();
    expect(operation.pending.value).toBe(false);
    expect(seen.at(-1)).toBe(true);

    for (const [status, kind] of [
      [{ contract: "test", requestId: "next", kind: "failed", error: { kind: "failed", message: "boom" } }, "failed"],
      [{ contract: "test", requestId: "next", kind: "timed-out", cancellation: "cancellation-requested" }, "timeout"],
    ] as const) {
      current = fakeOperation("next");
      const run = operation.run();
      expect(operation.cancelled.value).toBe(false);
      current.finish(status);
      expect(await run).toBeUndefined();
      expect(operation.cancelled.value).toBe(false);
      expect((operation.error.value as BridgeError).kind).toBe(kind);
    }
    operation.reset();
    expect(operation.error.value).toBeUndefined();
    scope.stop();
  });
});
