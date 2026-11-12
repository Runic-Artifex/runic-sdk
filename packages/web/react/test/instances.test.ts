// @vitest-environment happy-dom
// Runic instances: two guests in one document, each under its own RunicInstanceProvider, connect the same route to their
// own Bridges and show their own state, connection state and translated text.
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, type MockBridge, type MockView } from "@runic-artifex/views/mock";
import {
  configureTranslatedText, createRunicInstance, RunicInstanceProvider, useBridgeConnection, useRunicInstance, useText,
  useView, useWindowLocale, pageInstance, type RunicInstance, type TextReference,
} from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const roots: Root[] = [];
const cleanup: (() => void)[] = [];
afterEach(async () => {
  await act(async () => { for (const root of roots.splice(0)) root.unmount(); });
  for (const step of cleanup.splice(0).reverse()) step();
});

type Counter = { readonly count: number; readonly status: TextReference };
const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1_000), status: bridgeWire.textReference };
const counter = { connect: () => connectView<Counter>({ contract: "Tests.Counter:fp-1", route: "counter", members }) };
const status = (key: string): TextReference => ({ version: 2, catalog: "app", contractFingerprint: "sha256:1", key, arguments: {}, fallbackText: key });

interface Guest { readonly bridge: MockBridge; readonly view: MockView; readonly instance: RunicInstance }
function guest(count: number, key: string): Guest {
  const bridge = createMockBridge();
  const view = bridge.view("counter", { state: { count, status: status(key) } });
  const instance = createRunicInstance({ bridge });
  cleanup.push(() => instance.dispose());
  return { bridge, view, instance };
}

function Widget({ name }: { readonly name: string }): ReactNode {
  const view = useView(counter);
  const text = useText(view.state?.status);
  const locale = useWindowLocale();
  const connection = useBridgeConnection();
  const page = useRunicInstance().page;
  return createElement("p", { "data-name": name },
    `${view.state?.count ?? "…"}|${text}|${locale ?? "-"}|${connection.state}|${page ? "page" : "guest"}`);
}

async function mount(name: string, instance: RunicInstance | undefined): Promise<HTMLElement> {
  const container = document.body.appendChild(document.createElement("div"));
  const element = createElement(Widget, { name });
  await act(async () => {
    const root = createRoot(container);
    roots.push(root);
    root.render(instance ? createElement(RunicInstanceProvider, { instance }, element) : element);
  });
  return container;
}

async function settle(...guests: Guest[]): Promise<void> {
  for (let round = 0; round < 5; round++) {
    await act(async () => {
      for (const { bridge } of guests) await bridge.flush();
      await new Promise(resolve => setTimeout(resolve, 20));
    });
  }
}

describe("Runic instances", () => {
  test("two providers in one document connect the same route to their own Bridges", async () => {
    const a = guest(1, "alpha"), b = guest(2, "beta");
    cleanup.push(a.instance.run(() => configureTranslatedText({
      resolve: (reference, locale) => `${locale}:${reference.key}`, getLocale: () => "en",
    })));
    const first = await mount("a", a.instance);
    const second = await mount("b", b.instance);
    await settle(a, b);
    expect(first.textContent).toBe("1|en:alpha|-|connected|guest");
    expect(second.textContent).toBe("2|beta|-|connected|guest");

    await act(async () => { a.view.update({ count: 5 }); b.bridge.setWindowLocale("de"); });
    await settle(a, b);
    expect(first.textContent).toBe("5|en:alpha|-|connected|guest");
    expect(second.textContent).toBe("2|beta|de|connected|guest");

    await act(async () => { b.bridge.disconnect(); });
    await settle(a);
    expect(first.textContent).toBe("5|en:alpha|-|connected|guest");
    expect(second.textContent).toMatch(/^2\|beta\|de\|reconnecting\|guest$/);
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
    expect(b.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("without a provider a component uses the page instance", async () => {
    const container = await mount("page", undefined);
    expect(container.textContent).toMatch(/\|page$/);
    expect(pageInstance().page).toBe(true);
  });
});
