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

test("renders on the server without a DOM registry", () => {
  expect(renderToString(createElement(RunicHostedRegion, { name: "check", fallback: createElement("p", null, "Not running") })))
    .toBe('<runic-hosted-region name="check"><div slot="fallback" style="display:contents"><p>Not running</p></div></runic-hosted-region>');
});
