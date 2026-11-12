// @vitest-environment happy-dom
// Runic instances: two apps in one document, each under its own provideRunicInstance, connect the same route to their
// own Bridges and show their own state, connection state, translated text and window locale.
import { createApp, defineComponent, h, nextTick, type App } from "vue";
import { afterEach, describe, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, type MockBridge, type MockView } from "@runic-artifex/views/mock";
import {
  configureTranslatedText, createRunicInstance, pageInstance, provideRunicInstance, runicInstanceKey, useBridgeConnection,
  useRunicInstance, useText, useTextLocale, useView, useWindowLocale, type RunicInstance, type TextReference,
} from "../dist/index.js";

const apps: App[] = [];
const cleanup: (() => void)[] = [];
afterEach(() => {
  for (const app of apps.splice(0)) app.unmount();
  for (const step of cleanup.splice(0).reverse()) step();
  document.body.innerHTML = "";
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

const Widget = defineComponent({
  setup() {
    const view = useView(counter);
    const text = useText(() => view.state.value?.status);
    const locale = useWindowLocale();
    const textLocale = useTextLocale();
    const connection = useBridgeConnection();
    const page = useRunicInstance().page;
    return () => h("p",
      `${view.state.value?.count ?? "…"}|${text.value}|${locale.value ?? "-"}|${textLocale.value ?? "-"}|${connection.state.value}|${page ? "page" : "guest"}`);
  },
});

function mount(instance: RunicInstance | undefined, how: "provide" | "app" = "provide"): HTMLElement {
  const container = document.body.appendChild(document.createElement("div"));
  const root = instance && how === "provide"
    ? defineComponent({ setup() { provideRunicInstance(instance); return () => h(Widget); } })
    : Widget;
  const app = createApp(root);
  if (instance && how === "app") app.provide(runicInstanceKey, instance);
  apps.push(app);
  app.mount(container);
  return container;
}

async function settle(...guests: Guest[]): Promise<void> {
  for (let round = 0; round < 5; round++) {
    for (const { bridge } of guests) await bridge.flush();
    await new Promise(resolve => setTimeout(resolve, 20));
    await nextTick();
  }
}

describe("Runic instances", () => {
  test("two apps in one document connect the same route to their own Bridges", async () => {
    const a = guest(1, "alpha"), b = guest(2, "beta");
    cleanup.push(a.instance.run(() => configureTranslatedText({
      resolve: (reference, locale) => `${locale}:${reference.key}`, getLocale: () => "en",
    })));
    const first = mount(a.instance);
    const second = mount(b.instance, "app");
    await settle(a, b);
    expect(first.textContent).toBe("1|en:alpha|-|en|connected|guest");
    expect(second.textContent).toBe("2|beta|-|-|connected|guest");

    a.view.update({ count: 5 });
    b.bridge.setWindowLocale("de");
    await settle(a, b);
    expect(first.textContent).toBe("5|en:alpha|-|en|connected|guest");
    expect(second.textContent).toBe("2|beta|de|de|connected|guest");

    b.bridge.disconnect();
    await settle(a);
    expect(first.textContent).toBe("5|en:alpha|-|en|connected|guest");
    expect(second.textContent).toBe("2|beta|de|de|reconnecting|guest");
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
    expect(b.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("without a provider a component uses the page instance", () => {
    const container = mount(undefined);
    expect(container.textContent).toMatch(/\|page$/);
    expect(pageInstance().page).toBe(true);
  });
});
