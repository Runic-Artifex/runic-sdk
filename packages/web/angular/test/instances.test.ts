// Runic instances: two guests in one document, each a component that provides its own instance, connect the same route
// to their own Bridges and show their own state, connection state, translated text and window locale.
import "@angular/compiler";
import { Component, createEnvironmentInjector, EnvironmentInjector, runInInjectionContext, type Type } from "@angular/core";
import { TestBed, type ComponentFixture } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, type MockBridge, type MockView } from "@runic-artifex/views/mock";
import {
  configureTranslatedText, createRunicInstance, injectBridgeConnection, injectRunicInstance, injectText, injectView,
  injectWindowLocale, pageInstance, provideRunicInstance, RUNIC_INSTANCE, RunicTextPipe, textLocaleSignal,
  type RunicInstance, type TextReference,
} from "../dist/esm/index.js";

type Counter = { readonly count: number; readonly status: TextReference };
const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1_000), status: bridgeWire.textReference };
const counter = { connect: () => connectView<Counter>({ contract: "Tests.Counter:fp-1", route: "counter", members }) };
const status = (key: string): TextReference => ({ version: 2, catalog: "app", contractFingerprint: "sha256:1", key, arguments: {}, fallbackText: key });

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
const cleanup: (() => void)[] = [];
afterEach(() => {
  TestBed.resetTestingModule();
  for (const step of cleanup.splice(0).reverse()) step();
});

interface Guest { readonly bridge: MockBridge; readonly view: MockView; readonly instance: RunicInstance }
function guest(count: number, key: string): Guest {
  const bridge = createMockBridge();
  const view = bridge.view("counter", { state: { count, status: status(key) } });
  const instance = createRunicInstance({ bridge });
  cleanup.push(() => instance.dispose());
  return { bridge, view, instance };
}

// A component that shows what it reads through its instance; `instance` undefined provides none.
function widget(instance: RunicInstance | undefined): Type<unknown> {
  class Widget {
    readonly view = injectView(counter);
    readonly text = injectText(() => this.view.state()?.status);
    readonly locale = injectWindowLocale();
    readonly connection = injectBridgeConnection();
    readonly page = injectRunicInstance().page;
  }
  Component({
    selector: "test-widget",
    standalone: true,
    imports: [RunicTextPipe],
    providers: instance ? [provideRunicInstance(instance)] : [],
    template: `{{ view.state()?.count ?? "…" }}|{{ text() }}|{{ view.state()?.status | runicText }}|{{ locale() ?? "-" }}|`
      + `{{ connection.state() }}|{{ page ? "page" : "guest" }}`,
  })(Widget);
  return Widget;
}

async function settle(fixtures: readonly ComponentFixture<unknown>[], ...guests: Guest[]): Promise<void> {
  for (let round = 0; round < 5; round++) {
    for (const { bridge } of guests) await bridge.flush();
    await new Promise(resolve => setTimeout(resolve, 20));
    for (const fixture of fixtures) fixture.detectChanges();
  }
}

const text = (fixture: ComponentFixture<unknown>) => (fixture.nativeElement as HTMLElement).textContent;

describe("Runic instances", () => {
  test("two components that provide instances connect the same route to their own Bridges", async () => {
    const a = guest(1, "alpha"), b = guest(2, "beta");
    cleanup.push(a.instance.run(() => configureTranslatedText({
      resolve: (reference, locale) => `${locale}:${reference.key}`, getLocale: () => "en",
    })));
    const first = TestBed.createComponent(widget(a.instance));
    const second = TestBed.createComponent(widget(b.instance));
    await settle([first, second], a, b);
    expect(text(first)).toBe("1|en:alpha|en:alpha|-|connected|guest");
    expect(text(second)).toBe("2|beta|beta|-|connected|guest");

    a.view.update({ count: 5 });
    b.bridge.setWindowLocale("de");
    await settle([first, second], a, b);
    expect(text(first)).toBe("5|en:alpha|en:alpha|-|connected|guest");
    expect(text(second)).toBe("2|beta|beta|de|connected|guest");
    expect(textLocaleSignal(a.instance)()).toBe("en");
    expect(textLocaleSignal(b.instance)()).toBe("de");
    expect(textLocaleSignal(a.instance), "one signal per instance").toBe(textLocaleSignal(a.instance));

    b.bridge.disconnect();
    await settle([first, second], a);
    expect(text(first)).toBe("5|en:alpha|en:alpha|-|connected|guest");
    expect(text(second)).toBe("2|beta|beta|de|reconnecting|guest");
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
    expect(b.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("an environment injector's instance applies to injectors passed as options", async () => {
    const a = guest(3, "gamma");
    const injector = createEnvironmentInjector([provideRunicInstance(a.instance)], TestBed.inject(EnvironmentInjector));
    cleanup.push(() => injector.destroy());
    expect(injectRunicInstance({ injector })).toBe(a.instance);
    expect(runInInjectionContext(injector, () => injectRunicInstance())).toBe(a.instance);
    const view = injectView(counter, { injector });
    TestBed.tick();
    for (let round = 0; round < 5 && view.state() === undefined; round++) {
      await a.bridge.flush();
      await new Promise(resolve => setTimeout(resolve, 20));
      TestBed.tick();
    }
    expect(view.state()?.count).toBe(3);
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("without a provider the page instance is used", () => {
    const fixture = TestBed.createComponent(widget(undefined));
    fixture.detectChanges();
    expect(text(fixture)).toMatch(/\|page$/);
    expect(TestBed.inject(RUNIC_INSTANCE)).toBe(pageInstance());
    expect(TestBed.runInInjectionContext(() => injectRunicInstance())).toBe(pageInstance());
    expect(textLocaleSignal()).toBe(textLocaleSignal(pageInstance()));
  });
});
