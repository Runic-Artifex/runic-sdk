import "@angular/compiler";
import { Component, createEnvironmentInjector, effect, EnvironmentInjector, Injector, NgZone, PLATFORM_ID, signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test, expectTypeOf } from "vitest";
import {
  injectBridgeConnection, injectCollectionViewport, injectCommand, injectLatestOperation, injectMenuBar, injectOperation, injectView, RunicViewOutlet,
  ViewOutlet, type BridgeConnectionHandle, type MenuBarHandle, type LatestOperationHandle, type ViewRegistry,
} from "../dist/esm/index.js";
import * as root from "../dist/esm/index.js";
import {
  BridgeError, bridgeFailure, bridgeSuccess, type BridgeConnectionState, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome,
  type MenuBarMenu, type MenuBarRequest, type MenuBarState, type RunicBridgeClient,
} from "@runic-artifex/views";
import { installMockMenuBar } from "@runic-artifex/views/mock";

type Page =
  | { readonly kind: "counter"; connect(): Promise<unknown> }
  | { readonly kind: "welcome"; connect(): Promise<unknown> };

const external = signal(0);
let counterMounts = 0;

// Vitest does not run the Angular compiler, so these JIT components apply
// Component() directly and declare classic inputs; ngc-built consumers check
// signal-input registries.
class CounterPage {
  page?: Page;
  constructor() {
    counterMounts++;
    // Signal reads and effects (as injectView creates) while the outlet creates this component
    // and sets its input must not become outlet dependencies or fail.
    external();
    effect(() => {});
  }
}
Component({ selector: "test-counter", standalone: true, inputs: [{ name: "page", transform: (page: Page) => { external(); return page; } }], template: "counter:{{ page?.kind }}" })(CounterPage);

class WelcomePage {
  page?: Page;
}
Component({ selector: "test-welcome", standalone: true, inputs: ["page"], template: "welcome:{{ page?.kind }}" })(WelcomePage);

const registry = { counter: CounterPage, welcome: WelcomePage } as unknown as ViewRegistry<Page>;

class Host {
  readonly content = signal<Page | undefined>(undefined);
  readonly registry = signal<ViewRegistry<Page>>(registry);
}
Component({
  selector: "test-host",
  standalone: true,
  imports: [ViewOutlet],
  template: `<runic-view-outlet [content]="content()" [registry]="registry()" />`,
})(Host);

// angular-03: projected content is the fallback while there is no reference.
class FallbackHost {
  readonly content = signal<Page | undefined>(undefined);
  readonly registry = registry;
}
Component({
  selector: "test-fallback-host",
  standalone: true,
  imports: [ViewOutlet],
  template: `<runic-view-outlet [content]="content()" [registry]="registry"><span class="fallback">Loading</span></runic-view-outlet>`,
})(FallbackHost);

// X10: a missing template replaces the English alert.
class MissingHost {
  readonly content = signal<Page | undefined>({ kind: "welcome", connect: async () => undefined });
  readonly registry = signal<ViewRegistry<Page>>({ counter: CounterPage } as unknown as ViewRegistry<Page>);
}
Component({
  selector: "test-missing-host",
  standalone: true,
  imports: [ViewOutlet],
  template: `<runic-view-outlet [content]="content()" [registry]="registry()" [missing]="missingView" />
    <ng-template #missingView let-page let-same="content"><output>Keine Ansicht für {{ page.kind }}/{{ same.kind }}</output></ng-template>`,
})(MissingHost);

// The deprecated name of 0.7.0-preview.6 and earlier still renders the same outlet.
class LegacyHost {
  readonly content = signal<Page | undefined>({ kind: "welcome", connect: async () => undefined });
  readonly registry = registry;
}
Component({
  selector: "test-legacy-host",
  standalone: true,
  imports: [RunicViewOutlet],
  template: `<runic-view-outlet [content]="content()" [registry]="registry" />`,
})(LegacyHost);

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

const settle = async () => { await Promise.resolve(); await Promise.resolve(); TestBed.tick(); };

describe("injectView glue", () => {
  test("reports pending while a reference connects", async () => {
    let resolve!: (client: { snapshot: number; subscribe(listener: (state: number) => void): () => void; dispose(): void }) => void;
    const view = TestBed.runInInjectionContext(() => injectView({ connect: () => new Promise<Parameters<typeof resolve>[0]>(done => { resolve = done; }) }));
    expect(view.pending()).toBe(true);
    TestBed.tick();
    expect(view.pending()).toBe(true);
    resolve({ snapshot: 1, subscribe: listener => { listener(1); return () => {}; }, dispose() {} });
    await settle();
    expect(view.pending()).toBe(false);
    expect(view.state()).toBe(1);
  });
});

describe("ViewOutlet", () => {
  test("renders the registered component, remounts on a new reference and alerts for a missing kind", () => {
    counterMounts = 0;
    const fixture = TestBed.createComponent(Host);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(element.textContent?.trim()).toBe("");
    const counter: Page = { kind: "counter", connect: async () => undefined };
    fixture.componentInstance.content.set(counter);
    fixture.detectChanges();
    expect(element.textContent?.trim()).toBe("counter:counter");
    expect(counterMounts).toBe(1);
    external.update(value => value + 1);
    fixture.detectChanges();
    expect(counterMounts).toBe(1);
    fixture.componentInstance.content.set({ kind: "counter", connect: async () => undefined });
    fixture.detectChanges();
    expect(counterMounts).toBe(2);
    fixture.componentInstance.content.set({ kind: "welcome", connect: async () => undefined });
    fixture.detectChanges();
    expect(element.textContent?.trim()).toBe("welcome:welcome");
    fixture.componentInstance.registry.set({ counter: CounterPage } as unknown as ViewRegistry<Page>);
    fixture.detectChanges();
    expect(element.querySelector("[role=alert]")?.textContent).toBe("No web component is registered for welcome.");
  });

  test("renders projected content while there is no reference", () => {
    const fixture = TestBed.createComponent(FallbackHost);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(element.querySelector(".fallback")?.textContent).toBe("Loading");
    fixture.componentInstance.content.set({ kind: "welcome", connect: async () => undefined });
    fixture.detectChanges();
    expect(element.querySelector(".fallback")).toBeNull();
    expect(element.textContent?.trim()).toBe("welcome:welcome");
    fixture.componentInstance.content.set(undefined);
    fixture.detectChanges();
    expect(element.querySelector(".fallback")?.textContent).toBe("Loading");
  });

  test("renders the missing template for a kind without a component instead of the English alert", () => {
    counterMounts = 0;
    const fixture = TestBed.createComponent(MissingHost);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(element.textContent?.trim()).toBe("Keine Ansicht für welcome/welcome");
    expect(element.querySelector("[role=alert]")).toBeNull();
    fixture.componentInstance.content.set({ kind: "counter", connect: async () => undefined });
    fixture.detectChanges();
    expect(element.textContent?.trim()).toBe("counter:counter");
    expect(counterMounts).toBe(1);
  });

  test("injectCollectionViewport is a root export (graduated in 0.10)", () => {
    expect(typeof root.injectCollectionViewport).toBe("function");
  });

  test("the deprecated RunicViewOutlet alias renders the same outlet", () => {
    const fixture = TestBed.createComponent(LegacyHost);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe("welcome:welcome");
    expect(Object.getPrototypeOf(RunicViewOutlet)).toBe(ViewOutlet);
  });
});

describe("injectCommand", () => {
  test("exposes pending and error as signals and never rejects", async () => {
    const outcomes: (() => void)[] = [];
    const command = TestBed.runInInjectionContext(() => injectCommand((fail: boolean) => new Promise<number>((resolve, reject) => {
      outcomes.push(() => fail ? reject(new Error("rejected")) : resolve(1));
    })));
    const failed = command.run(true);
    expect(command.pending()).toBe(true);
    outcomes.shift()!();
    expect(await failed).toBeUndefined();
    expect(command.pending()).toBe(false);
    expect((command.error() as Error).message).toBe("rejected");
    const succeeded = command.run(false);
    expect(command.error()).toBeUndefined();
    outcomes.shift()!();
    expect(await succeeded).toBe(1);
  });

  test("keeps a declared failure apart from error; the latest run wins", async () => {
    type Fail = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
    const outcomes: ((value: BridgeOutcome<number, Fail> | Error) => void)[] = [];
    const command = TestBed.runInInjectionContext(() => injectCommand(() => new Promise<BridgeOutcome<number, Fail>>((resolve, reject) => {
      outcomes.push(value => value instanceof Error ? reject(value) : resolve(value));
    })));
    expectTypeOf(command.failure()).toEqualTypeOf<Fail | undefined>();
    const failed = command.run();
    outcomes[0]!(bridgeFailure({ $case: "titleRequired" }));
    await failed;
    expect(command.failure()).toEqual({ $case: "titleRequired" });
    expect(command.error()).toBeUndefined();
    const stale = command.run();
    const latest = command.run();
    expect(command.failure()).toBeUndefined();
    outcomes[2]!(bridgeSuccess(2));
    await latest;
    outcomes[1]!(bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
    await stale;
    expect(command.failure()).toBeUndefined();
    const broken = command.run();
    outcomes[3]!(new Error("broken"));
    await broken;
    expect((command.error() as Error).message).toBe("broken");
    expect(command.failure()).toBeUndefined();
    const again = command.run();
    outcomes[4]!(bridgeFailure({ $case: "titleRequired" }));
    await again;
    command.reset();
    expect(command.failure()).toBeUndefined();
    expect(command.error()).toBeUndefined();
    const plain = TestBed.runInInjectionContext(() => injectCommand(() => Promise.resolve(1)));
    expectTypeOf(plain.failure()).toEqualTypeOf<undefined>();
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
    const command = TestBed.runInInjectionContext(() => injectCommand((name: "save" | "publish" | "discard") =>
      name === "save" ? client.save() : name === "publish" ? client.publish() : client.discard()));
    expectTypeOf(command.run).returns.resolves.toEqualTypeOf<BridgeOutcome<State, SaveFailure> | BridgeOutcome<State, PublishFailure> | State | undefined>();
    expectTypeOf(command.failure()).toEqualTypeOf<SaveFailure | PublishFailure | undefined>();
    await command.run("save");
    expect(command.failure()).toEqual({ $case: "titleRequired" });
    expect(await command.run("discard")).toEqual({ title: "Discarded" });
    expect(command.failure()).toBeUndefined();
    const optional = TestBed.runInInjectionContext(() => injectCommand((name: "save" | "discard") => name === "save" ? undefined : client.discard()));
    expectTypeOf(optional.failure()).toEqualTypeOf<undefined>();
  });

  test("requires an injection context or an injector", () => {
    expect(() => injectCommand(() => undefined)).toThrow();
  });
});

describe("injectCollectionViewport", () => {
  test("measures the element signal and follows the options", () => {
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 200 });
    const target = signal<HTMLElement | undefined>(undefined);
    const count = signal(100);
    const viewport = TestBed.runInInjectionContext(() =>
      injectCollectionViewport(target, () => ({ totalCount: count(), rowHeight: 20, overscan: 0 })));
    TestBed.tick();
    expect(viewport()).toEqual({ start: 0, size: 0, offset: 0, totalSize: 2000 });
    target.set(element);
    TestBed.tick();
    expect(viewport()).toEqual({ start: 0, size: 10, offset: 0, totalSize: 2000 });
    count.set(5);
    TestBed.tick();
    expect(viewport()).toEqual({ start: 0, size: 5, offset: 0, totalSize: 100 });
  });

  test("attaches the container outside the Angular zone", () => {
    let outside = 0;
    const zone = { runOutsideAngular: <T>(run: () => T): T => { outside++; return run(); } };
    const injector = Injector.create({ providers: [{ provide: NgZone, useValue: zone }], parent: TestBed.inject(Injector) });
    const target = signal<HTMLElement | undefined>(document.createElement("div"));
    injectCollectionViewport(target, () => ({ totalCount: 10, rowHeight: 20 }), { injector });
    TestBed.tick();
    expect(outside).toBe(1);
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

describe("injectOperation", () => {
  test("Cancel ends in a cancelled state without an error; failed and timed-out runs are errors", async () => {
    let current = fakeOperation("first");
    const operation = TestBed.runInInjectionContext(() => injectOperation(() => current.operation));
    expectTypeOf(operation.cancelled()).toEqualTypeOf<boolean>();
    expectTypeOf(operation.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    const result = operation.run();
    expect(operation.pending()).toBe(true);
    await Promise.resolve();
    await operation.cancel();
    expect(current.cancels()).toBe(1);
    expect(operation.cancellationRequested()).toBe(true);
    current.finish({ contract: "test", requestId: "first", kind: "cancelled" });
    expect(await result).toBeUndefined();
    expect(operation.cancelled()).toBe(true);
    expect(operation.status()?.kind).toBe("cancelled");
    expect(operation.error()).toBeUndefined();
    expect(operation.pending()).toBe(false);

    for (const [status, kind] of [
      [{ contract: "test", requestId: "next", kind: "failed", error: { kind: "failed", message: "boom" } }, "failed"],
      [{ contract: "test", requestId: "next", kind: "timed-out", cancellation: "cancellation-requested" }, "timeout"],
    ] as const) {
      current = fakeOperation("next");
      const run = operation.run();
      expect(operation.cancelled()).toBe(false);
      current.finish(status);
      expect(await run).toBeUndefined();
      expect(operation.cancelled()).toBe(false);
      expect((operation.error() as BridgeError).kind).toBe(kind);
    }
    operation.reset();
    expect(operation.error()).toBeUndefined();
  });
});

describe("injectLatestOperation", () => {
  const drain = () => new Promise(resolve => setTimeout(resolve, 0));

  test("admits only the newest selection after the previous read finishes; fields are signals", async () => {
    const latest = TestBed.runInInjectionContext(() => injectLatestOperation<number>());
    expectTypeOf(latest).toEqualTypeOf<LatestOperationHandle<number>>();
    expectTypeOf(latest.blocked()).toEqualTypeOf<boolean>();
    expectTypeOf(latest.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    const first = fakeOperation("first");
    const second = fakeOperation("second");
    const started: string[] = [];
    const intent = (current: ReturnType<typeof fakeOperation>) =>
      ({ start: () => { started.push(current.operation.requestId); return current.operation; } });
    const a = latest.run(intent(first));
    expect(latest.pending()).toBe(true);
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
    expect(latest.outcome()).toEqual(bridgeSuccess(2));
    expect(latest.pending()).toBe(false);
  });

  test("blocks after uncertain admission until clear(\"replace\")", async () => {
    const latest = TestBed.runInInjectionContext(() => injectLatestOperation<number>());
    await latest.run({ start: () => Promise.reject(new Error("lost Start reply")) });
    expect(latest.blocked()).toBe(true);
    let started = 0;
    const next = fakeOperation("next");
    expect(await latest.run({ start: () => { started++; return next.operation; } })).toBeUndefined();
    expect(started).toBe(0);
    latest.clear("replace");
    expect(latest.blocked()).toBe(false);
    const run = latest.run({ start: () => { started++; return next.operation; } });
    next.finish({ contract: "test", requestId: "next", kind: "succeeded", result: 1 });
    expect(await run).toEqual(bridgeSuccess(1));
    expect(started).toBe(1);
  });

  test("destruction settles observation without cancelling accepted work", async () => {
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    const latest = injectLatestOperation<number>({ injector });
    const current = fakeOperation("lifetime");
    let completions = 0;
    const run = latest.run({ start: () => current.operation, waitForCompletion: () => { completions++; } });
    await drain();
    expect(latest.pending()).toBe(true);
    injector.destroy();
    expect(await run).toBeUndefined();
    current.finish({ contract: "test", requestId: "lifetime", kind: "succeeded", result: 1 });
    await drain();
    expect(completions).toBe(0);
    expect(current.cancels()).toBe(0);
    expect(latest.outcome()).toBeUndefined();
  });

  test("requires an injection context or an injector", () => {
    expect(() => injectLatestOperation()).toThrow();
  });
});

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

describe("injectBridgeConnection", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });

  test("follows the connection as a signal and unsubscribes when the injector is destroyed", () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    const connection = injectBridgeConnection({ injector });
    expectTypeOf(connection).toEqualTypeOf<BridgeConnectionHandle>();
    expectTypeOf(connection.state()).toEqualTypeOf<BridgeConnectionState>();
    expect(connection.state()).toBe("connected");
    expect(fake.listeners).toBe(1);
    fake.set(false);
    expect(connection.state()).toBe("reconnecting");
    fake.set(true);
    expect(connection.state()).toBe("connected");
    injector.destroy();
    expect(fake.listeners).toBe(0);
    fake.set(false);
    expect(connection.state()).toBe("connected");
  });

  test("a component's connection ends with the component; lostAfter is forwarded", () => {
    const fake = hostBridge(false);
    host.__runicBridge = fake.bridge;
    class Status { readonly connection = injectBridgeConnection({ lostAfter: 0 }); }
    Component({ selector: "test-status", standalone: true, template: "<output>{{ connection.state() }}</output>" })(Status);
    const fixture = TestBed.createComponent(Status);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toBe("lost");
    fake.set(true);
    fixture.detectChanges();
    expect(element.textContent).toBe("connected");
    fixture.destroy();
    expect(fake.listeners).toBe(0);
  });

  test("does not observe under server rendering", () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    const injector = createEnvironmentInjector([{ provide: PLATFORM_ID, useValue: "server" }], TestBed.inject(EnvironmentInjector));
    const connection = injectBridgeConnection({ injector });
    expect(connection.state()).toBe("connecting");
    expect(fake.listeners).toBe(0);
    injector.destroy();
  });

  test("requires an injection context or an injector, and is a root export (0.11)", () => {
    expect(() => injectBridgeConnection()).toThrow();
    expect(Object.keys(root).sort()).toEqual([
      "RunicViewOutlet", "ViewOutlet", "injectBridgeConnection", "injectCollectionViewport", "injectCommand", "injectLatestOperation",
      "injectMenuBar", "injectOperation", "injectView",
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
}

const keyDown = (init: KeyboardEventInit) => window.dispatchEvent(new KeyboardEvent("keydown", { cancelable: true, ...init }));

describe("injectMenuBar", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });

  test("follows the host as a signal, invokes, delivers requests and disposes with the injector", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux" });
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    const requests: MenuBarRequest[] = [];
    const bar = injectMenuBar({ injector, onRequest: request => requests.push(request) });
    expectTypeOf(bar).toEqualTypeOf<MenuBarHandle>();
    expectTypeOf(bar.state()).toEqualTypeOf<MenuBarState>();
    expect(bar.state().status).toBe("connecting");
    await until(() => bar.state().status === "ready");
    expect(bar.state().menus).toEqual(menuBarFixture);
    const save = menuBarFixture[0]!.entries[1]!;
    if (save.kind !== "item") throw new Error("Save is an item.");
    expect(await bar.invoke(save)).toBe("executed");
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    keyDown({ key: "F10", code: "F10" });
    keyDown({ key: "e", code: "KeyE", altKey: true });
    await until(() => mock.invocations.length === 2);
    expect(requests).toEqual([{ kind: "focus" }, { kind: "open", menu: bar.state().menus[1] }]);
    expect(bar.state().accessKeyHints).toBe(true);
    mock.update({ menus: menuBarFixture.slice(0, 1) });
    await until(() => bar.state().menus.length === 1);
    injector.destroy();
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(mock.invocations).toHaveLength(2);
  });

  test("a component's \"app\" lease ends with the component", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux" });
    class Bar { readonly menuBar = injectMenuBar({ shortcuts: "app" }); }
    Component({ selector: "test-menu-bar", standalone: true, template: "<output>{{ menuBar.state().status }} {{ menuBar.state().shortcuts }}</output>" })(Bar);
    const fixture = TestBed.createComponent(Bar);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toBe("connecting app");
    await until(() => mock.leased);
    fixture.detectChanges();
    expect(element.textContent).toBe("ready app");
    const event = new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true, cancelable: true });
    expect(fixture.componentInstance.menuBar.handleKeyDown(event)).toBe(true);
    await until(() => mock.invocations.length === 1);
    fixture.destroy();
    await until(() => !mock.leased);
  });

  test("creates no controller under server rendering", async () => {
    const mock = installMockMenuBar(menuBarFixture, { platform: "linux" });
    const injector = createEnvironmentInjector([{ provide: PLATFORM_ID, useValue: "server" }], TestBed.inject(EnvironmentInjector));
    const bar = injectMenuBar({ injector, shortcuts: "app" });
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(mock.leased).toBe(false);
    expect(bar.state()).toMatchObject({ status: "connecting", placement: "none", shortcuts: "app", menus: [] });
    expect(bar.handleKeyDown(new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true }))).toBe(false);
    await expect(bar.invoke({ kind: "item", key: "1m/0i", label: "New", accessKeyIndex: 0, commandId: "file.new", enabled: true }))
      .rejects.toMatchObject({ name: "BridgeError", kind: "unavailable" });
    injector.destroy();
  });

  test("requires an injection context or an injector", () => {
    expect(() => injectMenuBar()).toThrow();
  });
});
