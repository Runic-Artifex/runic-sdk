// Runs the page against the generated typed mocks, without .NET: `dotnet build` writes
// src/generated, including the *.mock.ts files, so build once before `test`.
import { TestBed, type ComponentFixture } from "@angular/core/testing";
import { afterEach, expect, test, vi } from "vitest";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { createTextReference } from "../../.runic/translations/transport.js";
import { startTranslations } from "../translations";
import { mockCounter } from "../generated/counter.mock.js";
import { mockWelcome } from "../generated/welcome.mock.js";
import { mockWorkspace } from "../generated/workspace.mock.js";
import { AppComponent } from "./app";

let fixture: ComponentFixture<AppComponent> | undefined;
let stopTranslations: (() => void) | undefined;
afterEach(() => { stopTranslations?.(); fixture?.destroy(); });

const page = () => fixture!.nativeElement as HTMLElement;
const text = () => page().textContent ?? "";
const button = (name: string): HTMLButtonElement => [...page().querySelectorAll("button")].find(element => element.textContent?.trim() === name)!;
// Waits until Angular has rendered the latest state.
const rendered = (check: () => void) => vi.waitFor(async () => { await fixture!.whenStable(); check(); });

test("the page connects, follows the window's language, increments the Counter and reports a reconnect and a close", async () => {
  // As main does: the page's Translations runtime, here in English until the window's locale arrives.
  stopTranslations = startTranslations("en");
  // Each test installs a new Bridge; generated clients then start from a new page runtime.
  const bridge = installMockBridge(createMockBridge());
  const welcome = mockWelcome(bridge, { id: "1", state: { greeting: createTextReference("welcome_greeting", {}, { fallbackText: "Your first Runic View is ready." }) } });
  const counter = mockCounter(bridge, {
    id: "2",
    state: { count: 0, step: 1, label: "Clicks" },
    commands: { increment: state => ({ count: state.count + state.step }) },
  });
  mockWorkspace(bridge, {
    state: { main: welcome.reference, language: "en" },
    commands: {
      showWelcome: () => ({ main: welcome.reference }),
      showCounter: () => ({ main: counter.reference }),
      chooseLanguage: (_state, language) => ({ language }),
    },
  });

  fixture = TestBed.createComponent(AppComponent);
  await rendered(() => expect(text()).toContain("Your first Runic View is ready."));
  await rendered(() => expect(text()).toContain("Connected to the .NET Window."));

  // The application switches the window's language: the ViewModel's text reference and the page's own text follow.
  // The language list shows every language of the catalog, each in its own language.
  expect([...page().querySelectorAll("option")].map(option => [option.value, option.textContent?.trim()])).toEqual([["de", "Deutsch"], ["en", "English"]]);
  bridge.setWindowLocale("de");
  await rendered(() => expect(text()).toContain("Deine erste Runic View ist bereit."));
  await rendered(() => expect(text()).toContain("Sprache"));
  expect(text()).toContain("Mit dem .NET-Fenster verbunden.");
  expect(document.documentElement.lang).toBe("de");
  button("Zähler").click();
  await rendered(() => expect(text()).toContain("Zähler-View"));
  expect(button("Erhöhen")).toBeDefined();
  button("Willkommen").click();
  bridge.setWindowLocale("en");
  await rendered(() => expect(text()).toContain("Your first Runic View is ready."));

  button("Counter").click();
  // Increment is enabled once the Counter client has connected.
  await rendered(() => expect(button("Increment").disabled).toBe(false));
  button("Increment").click();
  await rendered(() => expect(page().querySelector(".count")?.textContent?.trim()).toBe("1"));

  // While the Bridge reconnects, the page keeps the last state and says so.
  bridge.disconnect();
  await rendered(() => expect(text()).toContain("Reconnecting to the .NET Window…"));
  expect(page().querySelector(".count")?.textContent?.trim()).toBe("1");
  bridge.reconnect();
  await rendered(() => expect(text()).toContain("Connected to the .NET Window."));
  expect(text()).not.toContain("Reconnecting");

  // The application closes the Window: the page says so, also after the connection drops.
  expect(await bridge.closeWindow(1_000)).toBe(true);
  await rendered(() => expect(text()).toContain("The application closed this window."));
  bridge.disconnect();
  await rendered(() => expect(text()).toContain("The application closed this window."));
  expect(text()).not.toContain("Reconnecting");
});
