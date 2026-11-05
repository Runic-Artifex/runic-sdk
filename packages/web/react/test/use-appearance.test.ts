// @vitest-environment happy-dom
// appearance/1: useAppearance renders the page's appearance and follows the host's pushes.
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { afterEach, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useAppearance } from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

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

function Look() {
  const look = useAppearance();
  // The host's contrast: the store's own starts from the engine's media query (happy-dom's).
  return createElement("p", null, `${look.textScale ?? "-"} ${look.host?.contrast ?? "-"} ${look.accentColor ?? "-"}`);
}

test("renders the host's appearance and renders again on each push", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("shell", { state: {} });
  bridge.setAppearance({ textScale: 1.25, contrast: "forced-colors", accentColor: "#0078d4" });
  await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
  await bridge.flush();
  const container = await mount(createElement(Look));
  expect(container.textContent).toBe("1.25 forced-colors #0078d4");
  await act(async () => { bridge.setAppearance({ textScale: 2 }); await bridge.flush(); });
  expect(container.textContent).toMatch(/^2 /);
});

test("the server snapshot has every member null", () => {
  expect(renderToString(createElement(Look))).toContain("- ");
});
