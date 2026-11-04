// @vitest-environment happy-dom
// translation-text/1: useText renders a text reference in the window's language and follows a language switch.
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import { configureTranslatedText, useText, useWindowLocale, type TextReference } from "../dist/index.js";
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
