// @vitest-environment happy-dom
// translation-text/1: useText renders a text reference in the window's language and follows a language switch.
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { configureTranslatedText, useText, useTextLocale, useWindowLocale, ViewOutlet, type TextReference } from "../dist/index.js";
// The generated ESM of a real Runic Translations catalog (en, de), shared with @runic-artifex/views's tests.
import { m } from "../../views/test/fixtures/translations/generated/notes.esm-v5/messages.js";
import { configureLocaleResolver, createLocaleSource, localeDirection } from "../../views/test/fixtures/translations/generated/notes.esm-v5/runtime.js";
import { resolveTextReference } from "../../views/test/fixtures/translations/generated/notes.esm-v5/transport.js";
// A Translations runtime of two locales, as a generated transport.js and locale source would be.
function fakeTranslations() {
  const catalog: Record<string, Record<string, string>> = {
    en: { saved: "Saved.", title_too_long: "Title is too long." },
    de: { saved: "Gespeichert.", title_too_long: "Der Titel ist zu lang." },
  };
  let locale = "en";
  const listeners = new Set<(locale: string) => void>();
  return {
    resolve: (reference: { readonly key: string }, language: string) => catalog[language]?.[reference.key],
    getLocale: () => locale,
    setLocale(next: string) { locale = next; for (const listener of [...listeners]) listener(locale); },
    subscribe(listener: (locale: string) => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
}
const saved = { version: 2, catalog: "notes", contractFingerprint: "sha256:1", key: "saved", arguments: {} } as const;
const tooLong = { ...saved, key: "title_too_long", fallbackText: "Title must be short." } as const;

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let root: Root | undefined;
const cleanup: (() => void)[] = [];
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  for (const step of cleanup.splice(0)) step();
});

async function mount(element: ReactNode): Promise<HTMLElement> {
  const container = document.body.appendChild(document.createElement("div"));
  await act(async () => { root = createRoot(container); root.render(element); });
  return container;
}

function Status({ value, fallback }: { readonly value: TextReference | string | null | undefined; readonly fallback?: string }) {
  const text = useText(value, fallback);
  const locale = useWindowLocale();
  return createElement("p", { lang: locale ?? undefined }, text);
}

describe("useText", () => {
  test("renders the fallback text without a runtime, then the runtime's text, and follows a language switch", async () => {
    const container = await mount(createElement(Status, { value: tooLong }));
    expect(container.textContent).toBe("Title must be short.");
    const runtime = fakeTranslations();
    await act(async () => { cleanup.push(configureTranslatedText(runtime)); });
    expect(container.textContent).toBe("Title is too long.");
    await act(async () => { runtime.setLocale("de"); });
    expect(container.textContent).toBe("Der Titel ist zu lang.");
  });

  test("renders strings as they are and the fallback for nothing", async () => {
    const container = await mount(createElement("div", null,
      createElement(Status, { value: "plain" }), createElement(Status, { value: undefined, fallback: "Invalid value." })));
    expect(container.textContent).toBe("plainInvalid value.");
    expect(container.querySelector("p")!.getAttribute("lang")).toBeNull();
    void saved;
  });
});

describe("useTextLocale", () => {
  test("a component that renders m.*() renders again when the application switches the window's locale (S2)", async () => {
    const locale = createLocaleSource({ initialLocale: "en" });
    cleanup.push(configureLocaleResolver(locale.getLocale));
    cleanup.push(configureTranslatedText({
      ...locale,
      resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
      direction: localeDirection,
    }));
    const bridge = installMockBridge(createMockBridge());
    bridge.view("shell", { state: {} });
    await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
    let renders = 0;
    function Title() {
      const current = useTextLocale();
      renders++;
      return createElement("h1", { lang: current }, m.saved({ title: "Atlas" }));
    }
    const container = await mount(createElement(Title));
    expect(container.textContent).toBe("Saved Atlas.");
    await act(async () => { bridge.setWindowLocale("de"); await bridge.flush(); });
    expect(container.textContent).toBe("Atlas gespeichert.");
    expect(container.querySelector("h1")!.getAttribute("lang")).toBe("de");
    const before = renders;
    await act(async () => { bridge.setWindowLocale("de-AT"); await bridge.flush(); });
    expect(renders, "de-AT resolves to de: no change, no render").toBe(before);
    await act(async () => { bridge.setWindowLocale(null); bridge.setWindowLocale("en"); await bridge.flush(); });
    expect(container.textContent).toBe("Saved Atlas.");
  });
});

describe("ViewOutlet", () => {
  test("a kind without a component shows the development alert outside production builds", async () => {
    const container = await mount(createElement(ViewOutlet, {
      content: { kind: "missing" } as never, registry: {} as never,
    }));
    expect(container.querySelector("[role=alert]")!.textContent).toBe("No web component is registered for missing.");
  });
});
