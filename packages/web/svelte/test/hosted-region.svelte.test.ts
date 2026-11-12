// @vitest-environment happy-dom
// Hosted regions (experimental): RunicHostedRegion renders <runic-hosted-region> with its fallback snippet per state.
import { flushSync, mount, unmount } from "svelte";
import { expect, test } from "vitest";
import HostedRegionOwner from "./fixtures/HostedRegionOwner.svelte";

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
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) { await new Promise(resolve => setTimeout(resolve, 10)); flushSync(); }
  expect(condition()).toBe(true);
};

test("renders the element, declares the region and renders the fallback of each state", async () => {
  const states: string[] = [];
  const target = document.body.appendChild(document.createElement("div"));
  const component = mount(HostedRegionOwner, { target, props: { states } });
  flushSync();
  const region = target.querySelector("runic-hosted-region")!;
  expect([region.getAttribute("name"), region.getAttribute("sizing"), region.getAttribute("label"), region.className]).toEqual(["check", "content-height", "System check", "step"]);
  await until(() => requests.some(request => request["kind"] === "hosted-region-declare" && request["sizing"] === "content-height"));
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback waiting");
  push({ kind: "hosted-region-empty", name: "check", reason: "ended" });
  await until(() => region.querySelector("[slot=fallback]")!.textContent === "fallback ended");
  expect(states).toEqual(["ended"]);
  unmount(component);
  await until(() => requests.some(request => request["kind"] === "hosted-region-remove"));
});
