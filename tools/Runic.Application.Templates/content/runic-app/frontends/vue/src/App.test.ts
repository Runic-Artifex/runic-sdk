// @vitest-environment happy-dom
// Runs the page against the generated typed mocks, without .NET: `dotnet build` writes
// src/generated, including the *.mock.ts files, so build once before `test`.
import { createApp, type App as VueApp } from "vue";
import { afterEach, expect, test, vi } from "vitest";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { mockCounter } from "./generated/counter.mock.js";
import { mockWelcome } from "./generated/welcome.mock.js";
import { mockWorkspace } from "./generated/workspace.mock.js";
import App from "./App.vue";

let app: VueApp | undefined;
afterEach(() => { app?.unmount(); document.body.replaceChildren(); });

const text = () => document.body.textContent ?? "";
const button = (name: string): HTMLButtonElement => [...document.querySelectorAll("button")].find(element => element.textContent?.trim() === name)!;

test("the page connects, increments the Counter and reports a reconnect", async () => {
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

  app = createApp(App);
  app.mount(document.body.appendChild(document.createElement("div")));
  await vi.waitFor(() => expect(text()).toContain("Your first Runic View is ready."));
  await vi.waitFor(() => expect(text()).toContain("Connected to the .NET Window."));

  button("Counter").click();
  // Increment is enabled once the Counter client has connected.
  await vi.waitFor(() => expect(button("Increment").disabled).toBe(false));
  button("Increment").click();
  await vi.waitFor(() => expect(document.querySelector(".count")?.textContent?.trim()).toBe("1"));

  // While the Bridge reconnects, the page keeps the last state and says so.
  bridge.disconnect();
  await vi.waitFor(() => expect(text()).toContain("Reconnecting to the .NET Window…"));
  expect(document.querySelector(".count")?.textContent?.trim()).toBe("1");
  bridge.reconnect();
  await vi.waitFor(() => expect(text()).toContain("Connected to the .NET Window."));
  expect(text()).not.toContain("Reconnecting");
});
