// translation-text/1 on the client (specs/application/README.md, "Translated text"): the `text-reference` decoder, the
// `text` of validation errors, the window's locale that the producer pushes, and `resolveText` with the application's
// Translations runtime.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import {
  configureTranslatedText, createFieldController, onWindowLocale, resolveText, subscribeText, textLocale, windowLocale,
  type BridgeValidationState, type TextReference, type TextTranslations,
} from "../dist/index.js";
import { connectView, decodeBridgeValidation, fieldErrors } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

const titleTooLong: TextReference = {
  version: 2, catalog: "notes", contractFingerprint: "sha256:1", key: "title_too_long",
  arguments: { max: { type: "int64", value: "80" } }, fallbackText: "Title must be at most 80 characters.",
};
const saved: TextReference = { version: 2, catalog: "notes", contractFingerprint: "sha256:1", key: "saved", arguments: {} };

// A Translations runtime of two locales, as a generated `transport.js` and locale source would be.
function translations(initial = "en") {
  const catalog: Record<string, Record<string, (inputs: Record<string, unknown>) => string>> = {
    en: { title_too_long: inputs => `Title must be at most ${String(inputs["max"])} characters.`, saved: () => "Saved." },
    de: { title_too_long: inputs => `Der Titel darf höchstens ${String(inputs["max"])} Zeichen lang sein.`, saved: () => "Gespeichert." },
  };
  let locale = initial;
  const listeners = new Set<(locale: string) => void>();
  const runtime: TextTranslations & { readonly resolved: string[] } = {
    resolved: [],
    resolve(reference, language) {
      runtime.resolved.push(`${reference.key}@${language}`);
      const format = catalog[language]?.[reference.key];
      if (!format) return undefined;
      const inputs = Object.fromEntries(Object.entries(reference.arguments ?? {}).map(([name, argument]) =>
        [name, (argument as { value: unknown }).value]));
      return format(inputs);
    },
    getLocale: () => locale,
    setLocale(next) { locale = catalog[next] ? next : "en"; for (const listener of [...listeners]) listener(locale); },
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
  return runtime;
}

const cleanup: (() => void)[] = [];
let producer: MockBridge | undefined;
afterEach(async () => {
  for (const step of cleanup.splice(0).reverse()) step();
  if (producer) { producer.setWindowLocale(null); await producer.flush(); }
});

// A connected page whose mock producer sets the window's locale.
async function connected(): Promise<MockBridge> {
  if (producer) return producer;
  const bridge = createMockBridge();
  installMockBridge(bridge);
  bridge.view("status", { state: { status: saved } });
  await connectView<{ readonly status: TextReference }>({
    contract: "Tests.Status:fp-1", route: "status", members: { status: bridgeWire.textReference },
  });
  return producer = bridge;
}

async function reportWindowLocale(locale: string | null): Promise<void> {
  const bridge = await connected();
  bridge.setWindowLocale(locale);
  await bridge.flush();
}

test("the text-reference decoder accepts a reference and rejects other values", () => {
  assert.equal(bridgeWire.textReference(titleTooLong), titleTooLong);
  assert.deepEqual(bridgeWire.textReference({ version: 1, catalog: "c", contractFingerprint: "f", key: "k", arguments: { n: "1" } }),
    { version: 1, catalog: "c", contractFingerprint: "f", key: "k", arguments: { n: "1" } });
  for (const value of [null, "text", [], { ...saved, version: 3 }, { ...saved, key: "" }, { ...saved, catalog: 1 },
    { ...saved, arguments: [] }, { ...saved, fallbackText: 1 }, { ...saved, arguments: { bad: Number.NaN } }])
    assert.throws(() => bridgeWire.textReference(value), JSON.stringify(value));
  assert.throws(() => bridgeWire.textReference({ ...saved, fallbackText: "x".repeat(300_000) }), RangeError);
});

test("validation errors keep their text; a malformed text leaves the message", () => {
  const decoded = decodeBridgeValidation({
    hasErrors: true, truncated: false,
    errors: [
      { path: ["title"], message: "Title must be at most 80 characters.", code: "title-too-long", text: titleTooLong },
      { path: ["body"], message: "Body required", text: { key: 5 } },
    ],
  });
  assert.deepEqual(decoded.errors[0]!.text, titleTooLong);
  assert.equal("text" in decoded.errors[1]!, false);
});

test("resolveText shows the fallback text without a Translations runtime, and strings as they are", () => {
  assert.equal(resolveText(titleTooLong), "Title must be at most 80 characters.");
  assert.equal(resolveText(saved, "Saved"), "Saved", "the fallback argument when the reference has none");
  assert.equal(resolveText(saved), "saved", "the key last");
  assert.equal(resolveText("plain"), "plain");
  assert.equal(resolveText(undefined, "message"), "message");
  assert.equal(resolveText(null), "");
  assert.equal(textLocale(), undefined);
});

test("a configured runtime formats references in its locale, and a locale switch notifies subscribers", () => {
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  assert.equal(resolveText(titleTooLong), "Title must be at most 80 characters.");
  let notified = 0;
  cleanup.push(subscribeText(() => { notified++; }));
  runtime.setLocale!("de");
  assert.equal(notified, 1);
  assert.equal(textLocale(), "de");
  assert.equal(resolveText(titleTooLong), "Der Titel darf höchstens 80 Zeichen lang sein.");
  assert.equal(resolveText({ ...saved, key: "missing", fallbackText: "Fallback" }), "Fallback", "a key the runtime cannot format");
});

test("the window's locale sets the runtime's locale, also one pushed before the runtime was configured", async () => {
  const locales: (string | null)[] = [];
  cleanup.push(onWindowLocale(locale => { locales.push(locale); }));
  await reportWindowLocale("de");
  assert.equal(windowLocale(), "de");
  assert.equal(textLocale(), "de", "without a runtime, the window's locale");
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  assert.equal(runtime.getLocale(), "de");
  assert.equal(resolveText(saved), "Gespeichert.");
  await reportWindowLocale("en");
  assert.equal(resolveText(saved), "Saved.");
  await reportWindowLocale(null);
  assert.equal(runtime.getLocale(), "en", "null leaves the page's language");
  assert.deepEqual(locales, ["de", "en", null]);
});

test("a runtime without setLocale resolves in the window's locale", async () => {
  const runtime = translations();
  cleanup.push(configureTranslatedText({ resolve: runtime.resolve, getLocale: runtime.getLocale }));
  await reportWindowLocale("de");
  assert.equal(resolveText(saved), "Gespeichert.");
  assert.equal(runtime.getLocale(), "en");
});

test("fieldErrors shows a member's messages in the window's language and follows a switch", async () => {
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  const validation: BridgeValidationState = {
    hasErrors: true, truncated: false,
    errors: [{ path: ["title"], message: "Title must be at most 80 characters.", text: titleTooLong }, { path: ["title", "x"], message: "Below" }],
  };
  const own = ["Title must be at most 80 characters."];
  const english = fieldErrors(validation, "title", own);
  assert.deepEqual(english, ["Title must be at most 80 characters.", "Below"]);
  assert.equal(fieldErrors(validation, "title", own), english, "cached in one language");
  await reportWindowLocale("de");
  assert.deepEqual(fieldErrors(validation, "title", own),
    ["Der Titel darf höchstens 80 Zeichen lang sein.", "Below"], "the validation entry with a text stands for {name}Errors");
});

test("a field controller notifies its subscribers when the language changes", async () => {
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  type State = { readonly title: string; readonly validation: BridgeValidationState };
  const state: State = {
    title: "x".repeat(81),
    validation: { hasErrors: true, truncated: false, errors: [{ path: ["title"], message: "Too long", text: titleTooLong }] },
  };
  const field = createFieldController<State, object, string>({
    name: "title", read: current => current.title, write: async () => undefined, errors: current => fieldErrors(current.validation, "title"),
  });
  field.setSource({ state, client: {} });
  const seen: (readonly string[])[] = [];
  const stop = field.subscribe(() => { seen.push(field.current.errors); });
  assert.deepEqual(field.current.errors, ["Title must be at most 80 characters."]);
  await reportWindowLocale("de");
  assert.deepEqual(seen.at(-1), ["Der Titel darf höchstens 80 Zeichen lang sein."]);
  stop();
  field.dispose();
});

test("the runtime applies the window's locale that the mock producer pushes, after the hello and on each change", async () => {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  producer = undefined;
  bridge.view("status", { state: { status: saved } });
  bridge.setWindowLocale("de");
  const client = await connectView<{ readonly status: TextReference }>({
    contract: "Tests.Status:fp-2", route: "status", members: { status: bridgeWire.textReference },
  });
  producer = bridge;
  await bridge.flush();
  assert.equal(windowLocale(), "de", "pushed after the hello");
  assert.deepEqual(client.snapshot.status, saved);
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  assert.equal(resolveText(client.snapshot.status), "Gespeichert.");
  bridge.setWindowLocale("en");
  await bridge.flush();
  assert.equal(resolveText(client.snapshot.status), "Saved.");
  assert.equal(bridge.windowLocale, "en");
  assert.throws(() => bridge.setWindowLocale("not a tag"), RangeError);
});
