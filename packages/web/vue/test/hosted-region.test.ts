// @vitest-environment happy-dom
// Hosted regions (experimental): RunicHostedRegion renders <runic-hosted-region> with its fallback slot per state.
import { createApp, createSSRApp, h, nextTick } from "vue";
import { renderToString } from "vue/server-renderer";
import { expect, test } from "vitest";
import { RunicHostedRegion } from "../dist/experimental.js";

// A producer with hosted-regions/1 behind the page's Bridge: it records requests, and the test pushes.
const listeners = new Set<(message: string) => void>();
const requests: Record<string, unknown>[] = [];
(globalThis as { __runicBridge?: unknown }).__runicBridge = {
  isConnected: () => true,
  async request(text: string) {
    const message = JSON.parse(text) as Record<string, unknown>;
    requests.push(message);
    return message["kind"] === "hello"
      ? JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1"] })
      : '{"ok":true}';
  },
  onMessage(listener: (message: string) => void) { listeners.add(listener); return () => listeners.delete(listener); },
  onConnectionChange(listener: (connected: boolean) => void) { listener(true); return () => {}; },
};
const push = (message: object) => { for (const listener of listeners) listener(JSON.stringify(message)); };
const until = async (condition: () => boolean) => {
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
  expect(condition()).toBe(true);
};

test("renders the element, declares the region and renders the fallback slot of each state", async () => {
  const states: string[] = [];
  const container = document.body.appendChild(document.createElement("div"));
  const app = createApp({
    render: () => h(RunicHostedRegion, { name: "check", sizing: "content-height", label: "System check", class: "step", onStateChange: (state: string) => states.push(state) },
      { fallback: ({ state }: { state: string }) => h("p", `fallback ${state}`) }),
  });
  app.mount(container);
  const region = container.querySelector("runic-hosted-region")!;
  expect([region.getAttribute("name"), region.getAttribute("sizing"), region.getAttribute("label"), region.className]).toEqual(["check", "content-height", "System check", "step"]);
  await until(() => requests.some(request => request["kind"] === "hosted-region-declare" && request["sizing"] === "content-height"));
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback waiting");
  push({ kind: "hosted-region-empty", name: "check", reason: "ended" });
  await until(() => region.getAttribute("state") === "ended");
  await nextTick();
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback ended");
  expect(states).toEqual(["ended"]);
  app.unmount();
  await until(() => requests.some(request => request["kind"] === "hosted-region-remove"));
});

test("renders on the server", async () => {
  const html = await renderToString(createSSRApp({ render: () => h(RunicHostedRegion, { name: "check" }, { fallback: () => h("p", "Not running") }) }));
  expect(html.replace(/<!--\[-->|<!--\]-->/g, "")).toBe('<runic-hosted-region name="check"><div slot="fallback" style="display:contents;"><p>Not running</p></div></runic-hosted-region>');
});
