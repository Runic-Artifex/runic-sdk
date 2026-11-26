// @vitest-environment happy-dom
// Hosted regions (experimental): RunicHostedRegion renders <runic-hosted-region> with its fallback per state.
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { afterEach, expect, test } from "vitest";
import { RunicHostedRegion } from "../dist/experimental.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

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
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
  expect(condition()).toBe(true);
};

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
});

async function mount(element: ReactNode): Promise<HTMLElement> {
  const container = document.body.appendChild(document.createElement("div"));
  await act(async () => { root = createRoot(container); root.render(element); });
  return container;
}

test("renders the element, declares the region and renders the fallback of each state", async () => {
  const states: string[] = [];
  const container = await mount(createElement(RunicHostedRegion, {
    name: "check", sizing: "content-height", label: "System check", className: "step",
    fallback: state => createElement("p", null, `fallback ${state}`), onStateChange: state => states.push(state),
  }));
  const region = container.querySelector("runic-hosted-region")!;
  expect([region.getAttribute("name"), region.getAttribute("sizing"), region.getAttribute("label"), region.className]).toEqual(["check", "content-height", "System check", "step"]);
  expect(customElements.get("runic-hosted-region")).toBeDefined();
  await until(() => requests.some(request => request["kind"] === "hosted-region-declare" && request["sizing"] === "content-height"));
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback waiting");
  await act(async () => { push({ kind: "hosted-region-empty", name: "check", reason: "ended" }); });
  await until(() => region.getAttribute("state") === "ended");
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback ended");
  expect(states).toEqual(["ended"]);
});

const frame = (name: string) => ({ kind: "hosted-region-content", name, embedding: "frame", src: `http://127.0.0.1:9/_runic_frame/${name}`, title: null, allow: "" });

test("renders the stalled content of a stalled region, and its fallback for loading without it", async () => {
  const details: unknown[] = [];
  const fallback = (state: string, status: { stalled: boolean }) => createElement("p", null, `fallback ${state}${status.stalled ? " stalled" : ""}`);
  const container = await mount(createElement("div", null,
    createElement(RunicHostedRegion, {
      name: "stalled", fallback, stalled: createElement("p", null, "taking longer"), onStateChange: (_state, _previous, detail) => details.push(detail),
    }),
    createElement(RunicHostedRegion, { name: "loading", fallback })));
  const [withContent, without] = [...container.querySelectorAll("runic-hosted-region")];
  await until(() => ["stalled", "loading"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  await act(async () => { push(frame("stalled")); push(frame("loading")); });
  await until(() => withContent!.getAttribute("state") === "loading" && without!.getAttribute("state") === "loading");
  expect([shown(withContent!), shown(without!)]).toEqual(["fallback loading", "fallback loading"]);
  await act(async () => { loadFrame(withContent!); loadFrame(without!); });
  await until(() => withContent!.hasAttribute("stalled") && without!.hasAttribute("stalled"));
  await until(() => without!.querySelector("[slot=fallback]")!.textContent === "fallback loading stalled");
  expect(withContent!.querySelector("[slot=stalled]")!.textContent).toBe("taking longer");
  expect(without!.querySelector("[slot=stalled]")).toBeNull();
  expect([shown(withContent!), shown(without!)]).toEqual(["taking longer", "fallback loading stalled"]);
  expect(details.at(-1)).toEqual({ state: "loading", previous: "loading", stalled: true, reason: null });
});

test("renders the failed content with its reason, retry asks the host, and a region without it shows its fallback", async () => {
  const container = await mount(createElement("div", null,
    createElement(RunicHostedRegion, {
      name: "failed", fallback: createElement("p", null, "fallback"),
      failed: ({ failureReason, retry }) => createElement("button", { type: "button", onClick: retry }, `failed: ${failureReason}`),
    }),
    createElement(RunicHostedRegion, { name: "fallback", fallback: (state, { failureReason }) => createElement("p", null, `fallback ${state} ${failureReason}`) })));
  const [withContent, without] = [...container.querySelectorAll("runic-hosted-region")];
  await until(() => ["failed", "fallback"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  await act(async () => {
    push({ kind: "hosted-region-empty", name: "failed", reason: "failed", failure: "unreachable" });
    push({ kind: "hosted-region-empty", name: "fallback", reason: "failed", failure: "timeout" });
  });
  await until(() => withContent!.querySelector("[slot=failed]")!.textContent === "failed: unreachable");
  await until(() => without!.querySelector("[slot=fallback]")!.textContent === "fallback failed timeout");
  expect(without!.querySelector("[slot=failed]")).toBeNull();
  expect([shown(withContent!), shown(without!)]).toEqual(["failed: unreachable", "fallback failed timeout"]);
  await act(async () => { withContent!.querySelector("button")!.click(); });
  await until(() => requests.some(request => request["kind"] === "hosted-region-retry" && request["name"] === "failed"));
  expect(requests.find(request => request["kind"] === "hosted-region-retry")).not.toHaveProperty("src");
});

test("renders on the server without a DOM registry", () => {
  expect(renderToString(createElement(RunicHostedRegion, { name: "check", className: "step" }))).toBe('<runic-hosted-region class="step" name="check"></runic-hosted-region>');
  expect(renderToString(createElement(RunicHostedRegion, { name: "check", fallback: createElement("p", null, "Not running") })))
    .toBe('<runic-hosted-region name="check"><div slot="fallback" style="display:contents"><p>Not running</p></div></runic-hosted-region>');
});
