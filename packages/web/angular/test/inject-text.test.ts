// translation-text/1: injectText and the runicText pipe follow a language switch.
import "@angular/compiler";
import { ChangeDetectorRef, signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { configureTranslatedText, injectText, injectWindowLocale, RunicTextPipe } from "../dist/esm/index.js";
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

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
const cleanup: (() => void)[] = [];
afterEach(() => {
  TestBed.resetTestingModule();
  for (const step of cleanup.splice(0)) step();
});

describe("injectText", () => {
  test("is a signal that follows its source and the language", () => {
    const source = signal<typeof saved | typeof tooLong | undefined>(tooLong);
    const text = TestBed.runInInjectionContext(() => injectText(() => source(), () => "Invalid value."));
    const locale = TestBed.runInInjectionContext(() => injectWindowLocale());
    expect(text()).toBe("Title must be short.");
    expect(locale()).toBeNull();
    const runtime = fakeTranslations();
    cleanup.push(configureTranslatedText(runtime));
    expect(text()).toBe("Title is too long.");
    runtime.setLocale("de");
    expect(text()).toBe("Der Titel ist zu lang.");
    source.set(saved);
    expect(text()).toBe("Gespeichert.");
    source.set(undefined);
    expect(text()).toBe("Invalid value.");
  });
});

describe("RunicTextPipe", () => {
  test("shows the text and marks the view for check when the language changes", () => {
    let marked = 0;
    TestBed.configureTestingModule({ providers: [{ provide: ChangeDetectorRef, useValue: { markForCheck: () => { marked++; } } }] });
    const pipe = TestBed.runInInjectionContext(() => new RunicTextPipe());
    expect(pipe.transform(tooLong)).toBe("Title must be short.");
    expect(pipe.transform(undefined, "Invalid value.")).toBe("Invalid value.");
    const runtime = fakeTranslations();
    cleanup.push(configureTranslatedText(runtime));
    expect(marked).toBe(1);
    runtime.setLocale("de");
    expect(marked).toBe(2);
    expect(pipe.transform(tooLong)).toBe("Der Titel ist zu lang.");
    // Unchanged value, fallback and language: the last text, without formatting again.
    let resolved = 0;
    cleanup.push(configureTranslatedText({ ...runtime, resolve: (reference, language) => { resolved++; return runtime.resolve(reference, language); } }));
    const first = pipe.transform(tooLong);
    pipe.transform(tooLong);
    pipe.transform(tooLong);
    expect(resolved).toBe(1);
    expect(pipe.transform(tooLong)).toBe(first);
    pipe.transform(tooLong, "other");
    expect(resolved).toBe(2);
    runtime.setLocale("en");
    pipe.transform(tooLong, "other");
    expect(resolved).toBe(3);
    marked = 0;
    pipe.ngOnDestroy();
    runtime.setLocale("de");
    expect(marked).toBe(0);
  });
});
