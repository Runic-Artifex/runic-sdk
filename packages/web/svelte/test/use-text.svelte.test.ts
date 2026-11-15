// @vitest-environment happy-dom
// translation-text/1: useText follows a language switch reactively.
import { flushSync, mount, unmount } from "svelte";
import { describe, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { configureTranslatedText, useText, useTextLocale, useWindowLocale, type TextHandle } from "../src/views/index.js";
import TranslatedTitle from "./fixtures/TranslatedTitle.svelte";
import EditedTitle from "./fixtures/EditedTitle.svelte";
import { edited } from "./fixtures/edited-message.js";
// The generated ESM of a real Runic Translations catalog (en, de), shared with @runic-artifex/views's tests.
import { m } from "../../views/test/fixtures/translations/generated/notes.esm-v5/messages.js";
import { configureLocaleResolver, createLocaleSource } from "../../views/test/fixtures/translations/generated/notes.esm-v5/runtime.js";
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

describe("useText", () => {
  test("follows its source and the language while its owner lives", () => {
    let source = $state<typeof saved | typeof tooLong | undefined>(tooLong);
    let text!: TextHandle;
    let locale!: { readonly current: string | null };
    const seen: string[] = [];
    const destroy = $effect.root(() => {
      text = useText(() => source, () => "Invalid value.");
      locale = useWindowLocale();
      $effect(() => { seen.push(text.current); });
    });
    flushSync();
    expect(text.current).toBe("Title must be short.");
    expect(locale.current).toBeNull();
    const runtime = fakeTranslations();
    const stop = configureTranslatedText(runtime);
    flushSync();
    expect(text.current).toBe("Title is too long.");
    runtime.setLocale("de");
    flushSync();
    expect(text.current).toBe("Der Titel ist zu lang.");
    source = saved;
    flushSync();
    expect(text.current).toBe("Gespeichert.");
    source = undefined;
    flushSync();
    expect(seen.at(-1)).toBe("Invalid value.");
    destroy();
    stop();
  });
});

describe("useTextLocale", () => {
  test("a template that renders m.*() renders again when the application switches the window's locale (S2)", async () => {
    const locale = createLocaleSource({ initialLocale: "en" });
    const textLocale = useTextLocale();
    const stopResolver = configureLocaleResolver(() => textLocale.current ?? locale.getLocale());
    const stop = configureTranslatedText({
      ...locale,
      resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
    });
    const bridge = installMockBridge(createMockBridge());
    bridge.view("shell", { state: {} });
    await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
    const target = document.body.appendChild(document.createElement("div"));
    const component = mount(TranslatedTitle, { target });
    flushSync();
    expect(target.textContent).toBe("Saved Atlas.");
    bridge.setWindowLocale("de");
    await bridge.flush();
    flushSync();
    expect(target.textContent).toBe("Atlas gespeichert.");
    bridge.setWindowLocale("en");
    await bridge.flush();
    flushSync();
    expect(target.textContent).toBe("Saved Atlas.");
    unmount(component);
    stop();
    stopResolver();
  });
});

// The Runic Translations text-change channel as the Vite plugin's development client creates it (Translations 0.12.0):
// an in-place update swaps the message's text, increments `revision` and calls every listener.
const textChannelKey = Symbol.for("runic.translations.text");

describe("useTextLocale and in-place message updates (translations#176)", () => {
  test("a template that reads the text locale renders the edited text again and keeps its state, with no change to Svelte's helper", () => {
    const channel = { version: 1, revision: 0, listeners: new Set<() => void>() };
    (globalThis as Record<symbol, unknown>)[textChannelKey] = channel;
    const update = () => { channel.revision++; for (const listener of [...channel.listeners]) listener(); };
    edited.text = "Notes";
    const stop = configureTranslatedText(fakeTranslations());
    const target = document.body.appendChild(document.createElement("div"));
    const component = mount(EditedTitle, { target });
    try {
      flushSync();
      component.increment(); component.increment(); component.increment();
      flushSync();
      expect(target.textContent).toBe("Notes 3");
      edited.text = "My notes";
      update();
      flushSync();
      expect(target.textContent).toBe("My notes 3");
      edited.text = "Our notes";
      update();
      flushSync();
      expect(target.textContent, "every update, not only the first").toBe("Our notes 3");
    } finally {
      unmount(component);
      stop();
      delete (globalThis as Record<symbol, unknown>)[textChannelKey];
    }
  });
});
