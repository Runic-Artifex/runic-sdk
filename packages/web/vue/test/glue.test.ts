// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, shallowRef, watchEffect, type PropType } from "vue";
import { afterEach, describe, expect, test, expectTypeOf, vi } from "vitest";
import {
  useBridgeConnection, useCollectionViewport, useCommand, useLatestOperation, useMenuBar, useOperation, useView, ViewOutlet, type LatestOperationHandle,
  type MenuBarHandle, type ViewRegistry,
} from "../dist/index.js";
import * as root from "../dist/index.js";
import {
  BridgeError, bridgeFailure, bridgeSuccess, type BridgeConnectionState, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome,
  type MenuBarMenu, type MenuBarRequest, type MenuBarState, type RunicBridgeClient,
} from "@runic-artifex/views";
import { installMockMenuBar } from "@runic-artifex/views/mock";
import { renderToString } from "vue/server-renderer";

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

  test("renders the missing slot for a kind without a component instead of the English alert", async () => {
    const host = document.createElement("div");
    const content: Page = { kind: "welcome", connect: async () => fakeClient(1) };
    const app = createApp({
      setup: () => () => h(ViewOutlet<Page>, { content, registry: { counter: Counter } as unknown as ViewRegistry<Page> },
        { missing: ({ content: page }: { content: Page }) => h("output", `Keine Ansicht für ${page.kind}`) }),
    });
    app.mount(host);
    await nextTick();
    expect(host.textContent).toBe("Keine Ansicht für welcome");
    expect(host.querySelector("[role=alert]")).toBeNull();
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
  test("is a root export (graduated in 0.10)", () => {
    expect(typeof root.useCollectionViewport).toBe("function");
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
    useLatestOperation();
    useCollectionViewport({ totalCount: 0, rowHeight: 20 });
    // A Bridge with onConnectionChange is never polled, so the unreleased observer stays on this one.
    const stray = withBridge(hostBridge(false).bridge, () => useBridgeConnection());
    expect(warn.mock.calls.map(call => String(call[0]).split(" ")[0]))
      .toEqual(["useView", "useCommand", "useOperation", "useLatestOperation", "useCollectionViewport", "useBridgeConnection"]);
    expect(stray.state.value).toBe("connecting");
    expect(String(warn.mock.calls[0]![0])).toContain("outside an active effect scope");
  });

  test("composables inside an effect scope or a component do not warn", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const scope = effectScope();
    scope.run(() => {
      useView({ connect: () => new Promise<FakeClient>(() => {}) });
      useCommand(() => 1);
      useOperation(() => undefined);
      useLatestOperation();
      useCollectionViewport({ totalCount: 0, rowHeight: 20 });
      withBridge(hostBridge(false).bridge, () => useBridgeConnection());
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

describe("useLatestOperation", () => {
  const drain = () => new Promise(resolve => setTimeout(resolve, 0));

  test("admits only the newest selection after the previous read finishes; fields are shallow refs", async () => {
    const scope = effectScope();
    const outcomes: unknown[] = [];
    const latest = scope.run(() => {
      const handle = useLatestOperation<number>();
      watchEffect(() => { outcomes.push(handle.outcome.value); });
      return handle;
    })!;
    expectTypeOf(latest).toEqualTypeOf<LatestOperationHandle<number>>();
    expectTypeOf(latest.blocked.value).toEqualTypeOf<boolean>();
    expectTypeOf(latest.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    const first = fakeOperation("first");
    const second = fakeOperation("second");
    const started: string[] = [];
    const intent = (current: ReturnType<typeof fakeOperation>) =>
      ({ start: () => { started.push(current.operation.requestId); return current.operation; } });
    const a = latest.run(intent(first));
    expect(latest.pending.value).toBe(true);
    await drain();
    const b = latest.run(intent(second));
    await drain();
    expect(first.cancels()).toBe(1);
    expect(started).toEqual(["first"]);
    first.finish({ contract: "test", requestId: "first", kind: "cancelled" });
    expect(await a).toBeUndefined();
    await drain();
    expect(started).toEqual(["first", "second"]);
    second.finish({ contract: "test", requestId: "second", kind: "succeeded", result: 2 });
    expect(await b).toEqual(bridgeSuccess(2));
    await nextTick();
    expect(latest.pending.value).toBe(false);
    expect(outcomes.at(-1)).toEqual(bridgeSuccess(2));
    scope.stop();
  });

  test("blocks after uncertain admission until clear(\"replace\")", async () => {
    const scope = effectScope();
    const latest = scope.run(() => useLatestOperation<number>())!;
    await latest.run({ start: () => Promise.reject(new Error("lost Start reply")) });
    expect(latest.blocked.value).toBe(true);
    let started = 0;
    const next = fakeOperation("next");
    expect(await latest.run({ start: () => { started++; return next.operation; } })).toBeUndefined();
    expect(started).toBe(0);
    latest.clear("replace");
    expect(latest.blocked.value).toBe(false);
    const run = latest.run({ start: () => { started++; return next.operation; } });
    next.finish({ contract: "test", requestId: "next", kind: "succeeded", result: 1 });
    expect(await run).toEqual(bridgeSuccess(1));
    expect(started).toBe(1);
    scope.stop();
  });

  test("scope disposal settles observation without cancelling accepted work", async () => {
    const scope = effectScope();
    const latest = scope.run(() => useLatestOperation<number>())!;
    const current = fakeOperation("lifetime");
    let completions = 0;
    const run = latest.run({ start: () => current.operation, waitForCompletion: () => { completions++; } });
    await drain();
    expect(latest.pending.value).toBe(true);
    scope.stop();
    expect(await run).toBeUndefined();
    current.finish({ contract: "test", requestId: "lifetime", kind: "succeeded", result: 1 });
    await drain();
    expect(completions).toBe(0);
    expect(current.cancels()).toBe(0);
    expect(latest.outcome.value).toBeUndefined();
  });
});

function withBridge<T>(bridge: RunicBridgeClient, run: () => T): T {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  host.__runicBridge = bridge;
  try { return run(); } finally { delete host.__runicBridge; }
}

// A host script's Bridge: `onConnectionChange` reports the current state, then each change.
function hostBridge(initial: boolean) {
  let connected = initial;
  const listeners = new Set<(connected: boolean) => void>();
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    call: () => Promise.reject(new Error("not used")),
    onConnectionChange(listener) {
      listeners.add(listener);
      listener(connected);
      return () => { listeners.delete(listener); };
    },
  };
  return {
    bridge,
    get listeners() { return listeners.size; },
    set(value: boolean) { connected = value; for (const listener of [...listeners]) listener(value); },
  };
}

describe("useBridgeConnection", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });

  test("renders the connection state and unsubscribes when the component unmounts", async () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    const element = document.createElement("div");
    let seen: BridgeConnectionState[] = [];
    const app = createApp(defineComponent({
      setup() {
        const { state } = useBridgeConnection();
        expectTypeOf(state.value).toEqualTypeOf<BridgeConnectionState>();
        watchEffect(() => { seen = [...seen, state.value]; });
        return () => h("output", state.value);
      },
    }));
    app.mount(element);
    expect(element.textContent).toBe("connected");
    expect(fake.listeners).toBe(1);
    fake.set(false);
    await nextTick();
    expect(element.textContent).toBe("reconnecting");
    fake.set(true);
    await nextTick();
    expect(element.textContent).toBe("connected");
    expect(seen).toEqual(["connected", "reconnecting", "connected"]);
    app.unmount();
    expect(fake.listeners).toBe(0);
  });

  test("stopping the effect scope stops observing, and lostAfter is forwarded", () => {
    const fake = hostBridge(false);
    host.__runicBridge = fake.bridge;
    const scope = effectScope();
    const connection = scope.run(() => useBridgeConnection({ lostAfter: 0 }))!;
    expect(connection.state.value).toBe("lost");
    fake.set(true);
    expect(connection.state.value).toBe("connected");
    scope.stop();
    expect(fake.listeners).toBe(0);
    fake.set(false);
    expect(connection.state.value).toBe("connected");
  });

  test("is a root export (0.11)", () => {
    expect(typeof root.useBridgeConnection).toBe("function");
    expect(Object.keys(root).sort()).toEqual([
      "ViewOutlet", "useBridgeConnection", "useCollectionViewport", "useCommand", "useLatestOperation", "useMenuBar", "useOperation", "useView",
    ]);
  });
});

// A menu bar as Runic Desktop reports it on Linux (specs/desktop/menu-bar-page.md), served by the mock host routes.
// The lowest-peer run copies only this package, so the spec's fixture is not used here.
const menuBarFixture: readonly MenuBarMenu[] = [
  { kind: "menu", key: "1m", label: "File", accessKey: "F", accessKeyIndex: 0, role: "file", enabled: true, entries: [
    { kind: "item", key: "1m/0i", label: "New", accessKey: "N", accessKeyIndex: 0, commandId: "file.new", enabled: true,
      shortcut: "Primary+N", shortcutText: "Ctrl+N", ariaKeyShortcuts: "Control+N" },
    { kind: "item", key: "1m/1i", label: "Save", accessKey: "S", accessKeyIndex: 0, commandId: "file.save", enabled: true },
    { kind: "separator", key: "1m/2s" },
  ] },
  { kind: "menu", key: "0m", label: "Edit", accessKey: "E", accessKeyIndex: 0, role: "none", enabled: true, entries: [
    { kind: "item", key: "0m/0i", label: "Find", accessKey: "F", accessKeyIndex: 0, commandId: "edit.find", enabled: false },
  ] },
];

async function until(condition: () => boolean) {
  for (let round = 0; !condition(); round++) {
    if (round > 200) throw new Error("The condition was not met.");
    await new Promise(resolve => setTimeout(resolve, 2));
  }
  await nextTick();
}

const keyDown = (init: KeyboardEventInit) => window.dispatchEvent(new KeyboardEvent("keydown", { cancelable: true, ...init }));

describe("useMenuBar", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });

  test("renders the host's menus, invokes and delivers requests, and disposes when the component unmounts", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux" });
    const element = document.createElement("div");
    const requests: MenuBarRequest[] = [];
    let handle: MenuBarHandle | undefined;
    const app = createApp(defineComponent({
      setup() {
        handle = useMenuBar({ onRequest: request => requests.push(request) });
        expectTypeOf(handle.state.value).toEqualTypeOf<MenuBarState>();
        return () => h("output", `${handle!.state.value.status} ${handle!.state.value.menus.map(menu => menu.label).join(",")}`);
      },
    }));
    app.mount(element);
    expect(element.textContent).toBe("connecting ");
    await until(() => element.textContent === "ready File,Edit");
    expect(handle!.state.value.menus).toEqual(menuBarFixture);
    const save = menuBarFixture[0]!.entries[1]!;
    if (save.kind !== "item") throw new Error("Save is an item.");
    expect(await handle!.invoke(save)).toBe("executed");
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    await until(() => mock.invocations.length === 2);
    expect(mock.invocations.map(item => item.commandId)).toEqual(["file.save", "file.new"]);
    keyDown({ key: "F10", code: "F10" });
    keyDown({ key: "e", code: "KeyE", altKey: true });
    expect(requests).toEqual([{ kind: "focus" }, { kind: "open", menu: handle!.state.value.menus[1] }]);
    await nextTick();
    expect(handle!.state.value.accessKeyHints).toBe(true);
    mock.update({ menus: menuBarFixture.slice(0, 1) });
    await until(() => element.textContent === "ready File");
    app.unmount();
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(mock.invocations).toHaveLength(2);
  });

  test("shortcuts: \"app\" holds the lease until the effect scope stops", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux" });
    const scope = effectScope();
    const bar = scope.run(() => useMenuBar({ shortcuts: "app" }))!;
    expect(bar.state.value.shortcuts).toBe("app");
    await until(() => bar.state.value.status === "ready" && mock.leased);
    const event = new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true, cancelable: true });
    expect(bar.handleKeyDown(event)).toBe(true);
    expect(event.defaultPrevented).toBe(true);
    await until(() => mock.invocations.length === 1);
    scope.stop();
    await until(() => !mock.leased);
    mock.update({ menus: [] });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(bar.state.value.menus).toEqual(menuBarFixture);
  });

  test("server rendering creates no controller and stays connecting", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux", shortcuts: "page" });
    let handle: MenuBarHandle | undefined;
    const html = await renderToString(createApp(defineComponent({
      setup() {
        handle = useMenuBar({ shortcuts: "app" });
        return () => h("output", handle!.state.value.status);
      },
    })));
    expect(html).toBe("<output>connecting</output>");
    expect(handle!.state.value).toMatchObject({ status: "connecting", placement: "none", shortcuts: "app", menus: [] });
    expect(handle!.handleKeyDown(new KeyboardEvent("keydown", { key: "n", ctrlKey: true }))).toBe(false);
    await expect(handle!.invoke({ kind: "item", key: "1m/0i", label: "New", accessKeyIndex: 0, commandId: "file.new", enabled: true }))
      .rejects.toMatchObject({ name: "BridgeError", kind: "unavailable" });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(mock.leased).toBe(false);
  });
});
