import "@angular/compiler";
import { Component, effect, signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { injectCollectionViewport, injectCommand, injectView, RunicViewOutlet, type ViewRegistry } from "../dist/esm/index.js";

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
  imports: [RunicViewOutlet],
  template: `<runic-view-outlet [content]="content()" [registry]="registry()" />`,
})(Host);

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

describe("RunicViewOutlet", () => {
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
});
