// Runs the page against the generated typed mocks, without .NET: `dotnet build` writes
// src/generated, including the *.mock.ts files, so build once before `test`.
import { TestBed, type ComponentFixture } from "@angular/core/testing";
import { afterEach, expect, test, vi } from "vitest";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { mockCounter } from "../generated/counter.mock.js";
import { mockWelcome } from "../generated/welcome.mock.js";
import { mockWorkspace } from "../generated/workspace.mock.js";
import { AppComponent } from "./app";

let fixture: ComponentFixture<AppComponent> | undefined;
afterEach(() => { fixture?.destroy(); });

const page = () => fixture!.nativeElement as HTMLElement;
const text = () => page().textContent ?? "";
const button = (name: string): HTMLButtonElement => [...page().querySelectorAll("button")].find(element => element.textContent?.trim() === name)!;
// Waits until Angular has rendered the latest state.
const rendered = (check: () => void) => vi.waitFor(async () => { await fixture!.whenStable(); check(); });

test("the page connects, increments the Counter and reports a reconnect and a close", async () => {
  // Each test installs a new Bridge; generated clients then start from a new page runtime.
  const bridge = installMockBridge(createMockBridge());
  const welcome = mockWelcome(bridge, { id: "1", state: { greeting: "Your first Runic View is ready." } });
  const counter = mockCounter(bridge, {
    id: "2",
    state: { count: 0, step: 1, label: "Clicks" },
    commands: { increment: state => ({ count: state.count + state.step }) },
  });
  mockWorkspace(bridge, {
    state: { main: welcome.reference },
    commands: { showWelcome: () => ({ main: welcome.reference }), showCounter: () => ({ main: counter.reference }) },
  });

  fixture = TestBed.createComponent(AppComponent);
  await rendered(() => expect(text()).toContain("Your first Runic View is ready."));
  await rendered(() => expect(text()).toContain("Connected to the .NET Window."));

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
