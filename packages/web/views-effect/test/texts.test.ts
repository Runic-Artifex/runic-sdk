// translation-text/1: texts follows a text reference across language switches.
import { configureFieldErrorText, configureTranslatedText } from "@runic-artifex/views";
import { Effect, Fiber, Stream } from "effect";
import { describe, expect, test } from "vitest";
import { textLocales, texts } from "../src/index.js";
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
      let firstSeen!: () => void;
      const first = new Promise<void>(resolve => { firstSeen = resolve; });
      const fiber = Effect.runFork(Stream.runForEach(Stream.take(texts(tooLong), 2), text => Effect.sync(() => {
        seen.push(text);
        if (seen.length === 1) firstSeen();
      })));
      // Switch only after the consumer has the first text. A switch before that replaces the pending first signal (a slow
      // consumer skips to the latest change), so the stream would give only the German text and take(2) would wait forever.
      await first;
      runtime.setLocale("de");
      await Effect.runPromise(Fiber.join(fiber));
      expect(seen).toEqual(["Title is too long.", "Der Titel ist zu lang."]);
      void saved;
    } finally { stop(); }
  });

  test("textLocales and texts see a switch made while the consumer handles the first value", async () => {
    for (const stream of [() => textLocales(), () => texts(tooLong)]) {
      const runtime = fakeTranslations();
      const stop = configureTranslatedText(runtime);
      try {
        const seen: unknown[] = [];
        const fiber = Effect.runFork(Stream.runForEach(Stream.take(stream(), 2), value => Effect.promise(async () => {
          seen.push(value);
          // The handler awaits, as a render does, and the window's locale changes meanwhile.
          if (seen.length === 1) runtime.setLocale("de");
          await new Promise(resolve => setTimeout(resolve, 0));
        })));
        const joined = Effect.runPromise(Fiber.join(fiber));
        const timedOut = new Promise<"timeout">(resolve => setTimeout(() => resolve("timeout"), 1000));
        expect(await Promise.race([joined, timedOut])).not.toBe("timeout");
        expect(seen.length).toBe(2);
        expect(seen[1]).toBe(seen[0] === "en" ? "de" : "Der Titel ist zu lang.");
      } finally { stop(); }
    }
  });
});

// The Runic Translations text-change channel as the Vite plugin's development client creates it (Translations 0.12.0):
// an in-place update swaps the message's text, increments `revision` and calls every listener.
const textChannelKey = Symbol.for("runic.translations.text");

describe("textLocales and in-place message updates (translations#176)", () => {
  // Collects the stream's values while `steps` run, one step after each value.
  async function collect(steps: (() => void)[]): Promise<unknown[]> {
    const seen: unknown[] = [];
    const fiber = Effect.runFork(Stream.runForEach(Stream.take(textLocales(), steps.length + 1), value => Effect.promise(async () => {
      seen.push(value);
      steps[seen.length - 1]?.();
      await new Promise(resolve => setTimeout(resolve, 0));
    })));
    const joined = Effect.runPromise(Fiber.join(fiber)).then(() => "done" as const);
    const timedOut = new Promise<"timeout">(resolve => setTimeout(() => resolve("timeout"), 300));
    const outcome = await Promise.race([joined, timedOut]);
    if (outcome === "timeout") await Effect.runPromise(Fiber.interrupt(fiber));
    return seen;
  }

  test("emits the same locale again on each in-place update", async () => {
    const channel = { version: 1, revision: 0, listeners: new Set<() => void>() };
    (globalThis as Record<symbol, unknown>)[textChannelKey] = channel;
    const update = () => { channel.revision++; for (const listener of [...channel.listeners]) listener(); };
    const stop = configureTranslatedText(fakeTranslations());
    try {
      expect(await collect([update, update])).toEqual(["en", "en", "en"]);
    } finally {
      stop();
      delete (globalThis as Record<symbol, unknown>)[textChannelKey];
    }
  });

  test("without the channel, as in production, a text change that keeps the locale emits nothing", async () => {
    expect(textChannelKey in globalThis).toBe(false);
    const stop = configureTranslatedText(fakeTranslations());
    const stops: (() => void)[] = [];
    try {
      expect(await collect([() => { stops.push(configureFieldErrorText(() => undefined)); }])).toEqual(["en"]);
    } finally {
      for (const step of stops) step();
      stop();
    }
  });
});
