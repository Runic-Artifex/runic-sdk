// @vitest-environment happy-dom
// translation-text/1: useText follows a language switch as a computed ref.
import { effectScope, nextTick, shallowRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { ComputedRef } from "vue";
import { configureTranslatedText, useText, useWindowLocale } from "../dist/index.js";
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
