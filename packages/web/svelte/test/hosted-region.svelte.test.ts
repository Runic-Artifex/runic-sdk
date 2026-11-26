// @vitest-environment happy-dom
// Hosted regions (experimental): RunicHostedRegion renders <runic-hosted-region> with its fallback snippet per state.
import { flushSync, mount, unmount } from "svelte";
import { expect, test } from "vitest";
import HostedRegionOwner from "./fixtures/HostedRegionOwner.svelte";
import HostedRegionStatusOwner from "./fixtures/HostedRegionStatusOwner.svelte";

// The element's shadow roots are closed: keep them, to load a region's frame and read what its slots show.
const shadows = new WeakMap<Element, ShadowRoot>();
const attachShadow = Element.prototype.attachShadow;
Element.prototype.attachShadow = function (this: Element, init: ShadowRootInit) {
  const shadow = attachShadow.call(this, init);
  shadows.set(this, shadow);
  return shadow;
};
// happy-dom loads no frame page (and logs that it does not): the frame's load is the test's.
const loadFrame = (region: Element) => shadows.get(region)!.querySelector("iframe")!.dispatchEvent(new Event("load"));
// The text of the content the element's slots show: the state's slot, else the slot nested in it as its fallback
// content, and so on (happy-dom's flattened assignment does not follow fallback content).
const shown = (region: Element) => {
  for (let slot = shadows.get(region)!.querySelector("slot"); slot; slot = slot.querySelector(":scope > slot")) {
    const assigned = slot.assignedElements();
    if (assigned.length > 0) return assigned.map(element => element.textContent).join("|");
  }
  return "";
};

// A producer with hosted-regions/1 and hosted-region-loading/1 behind the page's Bridge: it records requests, and the test
// pushes.
const listeners = new Set<(message: string) => void>();
const requests: Record<string, unknown>[] = [];
(globalThis as { __runicBridge?: unknown }).__runicBridge = {
  isConnected: () => true,
  async request(text: string) {
    const message = JSON.parse(text) as Record<string, unknown>;
    requests.push(message);
    return message["kind"] === "hello"
      ? JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1", "hosted-region-loading/1"] })
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

const frame = (name: string) => ({ kind: "hosted-region-content", name, embedding: "frame", src: `http://127.0.0.1:9/_runic_frame/${name}`, title: null, allow: "" });

test("renders the stalled and failed snippets, retry asks the host, and a region without them shows its fallback", async () => {
  const details: unknown[] = [];
  const target = document.body.appendChild(document.createElement("div"));
  const component = mount(HostedRegionStatusOwner, { target, props: { details } });
  flushSync();
  const [withSnippets, without] = [...target.querySelectorAll("runic-hosted-region")];
  await until(() => ["with", "without"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  expect(component.retry()).toBe(false);

  // Stalled: the stalled snippet, else the fallback for loading.
  push(frame("with"));
  push(frame("without"));
  await until(() => withSnippets!.getAttribute("state") === "loading" && without!.getAttribute("state") === "loading");
  expect([shown(withSnippets!), shown(without!)]).toEqual(["fallback loading", "fallback loading "]);
  loadFrame(withSnippets!);
  loadFrame(without!);
  await until(() => withSnippets!.hasAttribute("stalled") && without!.hasAttribute("stalled"));
  flushSync();
  expect(without!.querySelector("[slot=stalled]")).toBeNull();
  expect([shown(withSnippets!), shown(without!)]).toEqual(["taking longer", "fallback loading stalled "]);
  expect(details.at(-1)).toEqual({ state: "loading", previous: "loading", stalled: true, reason: null });

  // Failed: the failed snippet with its reason, else the fallback for failed.
  push({ kind: "hosted-region-empty", name: "with", reason: "failed", failure: "unreachable" });
  push({ kind: "hosted-region-empty", name: "without", reason: "failed", failure: "timeout" });
  await until(() => withSnippets!.getAttribute("state") === "failed" && without!.getAttribute("state") === "failed");
  flushSync();
  expect(without!.querySelector("[slot=failed]")).toBeNull();
  expect([shown(withSnippets!), shown(without!)]).toEqual(["failed: unreachable", "fallback failed timeout"]);
  withSnippets!.querySelector("button")!.click();
  await until(() => requests.some(request => request["kind"] === "hosted-region-retry" && request["name"] === "with"));
  expect(requests.find(request => request["kind"] === "hosted-region-retry")).not.toHaveProperty("src");
  expect(component.retry()).toBe(true);
  unmount(component);
});
