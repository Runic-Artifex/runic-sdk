// @vitest-environment happy-dom
// Runic instances: two guests in one document, each under its own setRunicInstance, connect the same route to their
// own Bridges and show their own state, connection state, translated text and window locale.
import { flushSync, mount, unmount } from "svelte";
import { afterEach, describe, expect, test } from "vitest";
import { createMockBridge, type MockBridge, type MockView } from "@runic-artifex/views/mock";
import { configureTranslatedText, createRunicInstance, getRunicInstance, pageInstance, type RunicInstance, type TextReference } from "../src/index.js";
import InstanceHost from "./fixtures/InstanceHost.svelte";
import { instanceCounter } from "./fixtures/instance-counter.js";

const mounted: ReturnType<typeof mount>[] = [];
const cleanup: (() => void)[] = [];
afterEach(async () => {
  for (const component of mounted.splice(0)) await unmount(component);
  for (const step of cleanup.splice(0).reverse()) step();
  document.body.replaceChildren();
});

const status = (key: string): TextReference => ({ version: 2, catalog: "app", contractFingerprint: "sha256:1", key, arguments: {}, fallbackText: key });

interface Guest { readonly bridge: MockBridge; readonly view: MockView; readonly instance: RunicInstance }
function guest(count: number, key: string): Guest {
  const bridge = createMockBridge();
  const view = bridge.view("counter", { state: { count, status: status(key) } });
  const instance = createRunicInstance({ bridge });
  cleanup.push(() => instance.dispose());
  return { bridge, view, instance };
}

function show(instance: RunicInstance | undefined): HTMLElement {
  const target = document.body.appendChild(document.createElement("div"));
  mounted.push(mount(InstanceHost, { target, props: { instance, source: instanceCounter } }));
  flushSync();
  return target;
}

async function settle(...guests: Guest[]): Promise<void> {
  for (let round = 0; round < 5; round++) {
    for (const { bridge } of guests) await bridge.flush();
    await new Promise(resolve => setTimeout(resolve, 20));
    flushSync();
  }
}

describe("Runic instances", () => {
  test("two instances in one document connect the same route to their own Bridges", async () => {
    const a = guest(1, "alpha"), b = guest(2, "beta");
    cleanup.push(a.instance.run(() => configureTranslatedText({
      resolve: (reference, locale) => `${locale}:${reference.key}`, getLocale: () => "en",
    })));
    const first = show(a.instance);
    const second = show(b.instance);
    await settle(a, b);
    expect(first.textContent).toBe("1|en:alpha|-|connected|guest");
    expect(second.textContent).toBe("2|beta|-|connected|guest");

    a.view.update({ count: 5 });
    b.bridge.setWindowLocale("de");
    await settle(a, b);
    expect(first.textContent).toBe("5|en:alpha|-|connected|guest");
    expect(second.textContent).toBe("2|beta|de|connected|guest");

    b.bridge.disconnect();
    await settle(a);
    expect(first.textContent).toBe("5|en:alpha|-|connected|guest");
    expect(second.textContent).toBe("2|beta|de|reconnecting|guest");
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
    expect(b.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("without setRunicInstance a component uses the page instance", () => {
    const target = show(undefined);
    expect(target.textContent).toMatch(/\|page$/);
    expect(getRunicInstance()).toBe(pageInstance());
  });
});
