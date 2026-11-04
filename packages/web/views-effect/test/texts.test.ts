// translation-text/1: texts follows a text reference across language switches.
import { configureTranslatedText } from "@runic-artifex/views";
import { Effect, Fiber, Stream } from "effect";
import { describe, expect, test } from "vitest";
import { texts } from "../src/index.js";
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

describe("texts", () => {
  test("is the text now, then the text after each language switch", async () => {
    const runtime = fakeTranslations();
    const stop = configureTranslatedText(runtime);
    try {
      const seen: string[] = [];
      const fiber = Effect.runFork(Stream.runForEach(Stream.take(texts(tooLong), 2), text => Effect.sync(() => { seen.push(text); })));
      await Effect.runPromise(Effect.yieldNow);
      await new Promise(resolve => setTimeout(resolve, 0));
      runtime.setLocale("de");
      await Effect.runPromise(Fiber.join(fiber));
      expect(seen).toEqual(["Title is too long.", "Der Titel ist zu lang."]);
      void saved;
    } finally { stop(); }
  });
});
