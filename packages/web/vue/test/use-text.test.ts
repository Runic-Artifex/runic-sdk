// @vitest-environment happy-dom
// translation-text/1: useText follows a language switch as a computed ref.
import { createApp, defineComponent, effectScope, h, nextTick, ref, shallowRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { ComputedRef } from "vue";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { configureTranslatedText, useText, useTextLocale, useWindowLocale, ViewOutlet } from "../dist/index.js";
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
  test("follows its source and the language until the scope stops", async () => {
    const scope = effectScope();
    const source = shallowRef<typeof saved | typeof tooLong | string | undefined>(tooLong);
    const handles = scope.run(() => ({ text: useText(source, "Invalid value."), locale: useWindowLocale() }))!;
    expectTypeOf(handles.text).toEqualTypeOf<ComputedRef<string>>();
    expect(handles.text.value).toBe("Title must be short.");
    expect(handles.locale.value).toBeNull();
    const runtime = fakeTranslations();
    const stop = configureTranslatedText(runtime);
    expect(handles.text.value).toBe("Title is too long.");
    runtime.setLocale("de");
    await nextTick();
    expect(handles.text.value).toBe("Der Titel ist zu lang.");
    source.value = saved;
    expect(handles.text.value).toBe("Gespeichert.");
    source.value = undefined;
    expect(handles.text.value).toBe("Invalid value.");
    scope.stop();
    stop();
  });
});

describe("useTextLocale", () => {
  test("a template that renders m.*() renders again when the application switches the window's locale (S2)", async () => {
    const locale = createLocaleSource({ initialLocale: "en" });
    const textLocale = useTextLocale();
    expect(useTextLocale(), "one ref for the page").toBe(textLocale);
    const stopResolver = configureLocaleResolver(() => textLocale.value ?? locale.getLocale());
    const stop = configureTranslatedText({
      ...locale,
      resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
    });
    const bridge = installMockBridge(createMockBridge());
    bridge.view("shell", { state: {} });
    await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
    const container = document.body.appendChild(document.createElement("div"));
    const app = createApp(defineComponent(() => () => h("h1", m.saved({ title: "Atlas" }))));
    app.mount(container);
    expect(container.textContent).toBe("Saved Atlas.");
    bridge.setWindowLocale("de");
    await bridge.flush();
    await nextTick();
    expect(container.textContent).toBe("Atlas gespeichert.");
    bridge.setWindowLocale("en");
    await bridge.flush();
    await nextTick();
    expect(container.textContent).toBe("Saved Atlas.");
    app.unmount();
    stop();
    stopResolver();
  });
});

// The Runic Translations text-change channel as the Vite plugin's development client creates it (Translations 0.12.0):
// an in-place update swaps the message's text, increments `revision` and calls every listener.
const textChannelKey = Symbol.for("runic.translations.text");

describe("useTextLocale and in-place message updates (translations#176)", () => {
  test("a template that reads the text locale renders the edited text again and keeps its state, with no change to Vue's helper", async () => {
    const channel = { version: 1, revision: 0, listeners: new Set<() => void>() };
    (globalThis as Record<symbol, unknown>)[textChannelKey] = channel;
    const update = () => { channel.revision++; for (const listener of [...channel.listeners]) listener(); };
    // An edited message, as the swapped table of a generated message module formats it: it reads the text locale
    // through the page's locale resolver, as `m.*()` does.
    const edited = { text: "Notes" };
    const textLocale = useTextLocale();
    const title = () => { void textLocale.value; return edited.text; };
    const stop = configureTranslatedText(fakeTranslations());
    const count = ref(0);
    const container = document.body.appendChild(document.createElement("div"));
    const app = createApp(defineComponent(() => () => h("h1", `${title()} ${count.value}`)));
    app.mount(container);
    try {
      count.value = 3;
      await nextTick();
      expect(container.textContent).toBe("Notes 3");
      edited.text = "My notes";
      update();
      await nextTick();
      expect(container.textContent).toBe("My notes 3");
      edited.text = "Our notes";
      update();
      await nextTick();
      expect(container.textContent, "every update, not only the first").toBe("Our notes 3");
    } finally {
      app.unmount();
      stop();
      delete (globalThis as Record<symbol, unknown>)[textChannelKey];
    }
  });
});

describe("ViewOutlet", () => {
  test("a kind without a component shows the development alert outside production builds", () => {
    const container = document.body.appendChild(document.createElement("div"));
    const app = createApp(() => h(ViewOutlet as never, { content: { kind: "missing" }, registry: {} }));
    app.mount(container);
    expect(container.querySelector("[role=alert]")!.textContent).toBe("No web component is registered for missing.");
    app.unmount();
  });
});
