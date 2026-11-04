// translation-text/1 on the client (specs/application/README.md, "Translated text"): the `text-reference` decoder, the
// `text` of validation errors, the window's locale that the producer pushes, and `resolveText` with the application's
// Translations runtime.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import {
  bindField, configureTranslatedText, createFieldController, onWindowLocale, resolveText, subscribeText, textLocale, windowLocale,
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
  for (const value of [null, "text", [], { ...saved, version: 0 }, { ...saved, key: "" }, { ...saved, catalog: 1 },
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

test("a window locale the runtime refuses falls back through its parents before it is reported (L3-20)", async () => {
  // A runtime under the Exact policy, as the generated runtime is: setLocale throws for a locale it lacks.
  const runtime = translations();
  const tried: string[] = [];
  const strict: TextTranslations = {
    ...runtime,
    setLocale(next) {
      tried.push(next);
      if (next !== "de" && next !== "en") throw new RangeError(`Unsupported locale: ${next}`);
      runtime.setLocale!(next);
    },
  };
  cleanup.push(configureTranslatedText(strict));
  const reported: unknown[] = [];
  const previous = globalThis.reportError;
  globalThis.reportError = error => { reported.push(error); };
  cleanup.push(() => { globalThis.reportError = previous; });
  await reportWindowLocale("de-CH-u-co-phonebk");
  assert.deepEqual(tried, ["de-CH-u-co-phonebk", "de-CH", "de"]);
  assert.equal(runtime.getLocale(), "de");
  assert.equal(resolveText(saved), "Gespeichert.", "references follow the window's language");
  assert.equal(reported.length, 0, "a parent the runtime has is not an error");
  tried.length = 0;
  await reportWindowLocale("fr-CA");
  assert.deepEqual(tried, ["fr-CA", "fr"]);
  assert.equal(runtime.getLocale(), "de", "a locale without a parent the runtime has keeps the page's language");
  assert.equal(reported.length, 1, "the first error is reported once");
  assert.match(String(reported[0]), /fr-CA/);
});

test("the parent fallback never switches the script (L3-20)", async () => {
  const runtime = translations();
  const tried: string[] = [];
  const available = new Set(["de", "en", "zh", "sr", "zh-Hant", "sr-Latn"]);
  const strict: TextTranslations = {
    ...runtime,
    setLocale(next) {
      tried.push(next);
      if (!available.has(next)) throw new RangeError(`Unsupported locale: ${next}`);
      if (next === "de" || next === "en") runtime.setLocale!(next);
    },
  };
  cleanup.push(configureTranslatedText(strict));
  const reported: unknown[] = [];
  const previous = globalThis.reportError;
  globalThis.reportError = error => { reported.push(error); };
  cleanup.push(() => { globalThis.reportError = previous; });
  const follow = async (locale: string) => { tried.length = 0; await reportWindowLocale(locale); return [...tried]; };
  assert.deepEqual(await follow("zh-Hant-TW"), ["zh-Hant-TW", "zh-Hant"]);
  assert.deepEqual(await follow("sr-Latn-RS"), ["sr-Latn-RS", "sr-Latn"]);
  available.delete("zh-Hant");
  available.delete("sr-Latn");
  assert.deepEqual(await follow("zh-Hant-TW"), ["zh-Hant-TW", "zh-Hant"], "Traditional never falls back to zh (Simplified)");
  assert.deepEqual(await follow("zh-TW"), ["zh-TW"], "zh-TW implies Traditional");
  assert.deepEqual(await follow("sr-Latn"), ["sr-Latn"], "Latin never falls back to sr (Cyrillic)");
  assert.deepEqual(await follow("sr-ME"), ["sr-ME"], "sr-ME implies Latin");
  assert.equal(reported.length, 4, "each refused locale without a same-script parent is reported");
  assert.deepEqual(await follow("zh-Hans-CN"), ["zh-Hans-CN", "zh-Hans", "zh"]);
  assert.deepEqual(await follow("sr-Cyrl-RS"), ["sr-Cyrl-RS", "sr-Cyrl", "sr"]);
  assert.deepEqual(await follow("zh-CN"), ["zh-CN", "zh"]);
  assert.deepEqual(await follow("de-AT"), ["de-AT", "de"]);
  assert.equal(reported.length, 4);
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

test("the shape follows the Translations readers: arguments required, a null fallback only in version 1, any later version", () => {
  const v1 = bridgeWire.textReference({ version: 1, catalog: "c", contractFingerprint: "f", key: "k", arguments: {}, fallbackText: null });
  assert.equal(resolveText(v1, "message"), "message", "a version 1 null fallback is none");
  assert.equal(resolveText(v1), "k");
  assert.equal(bridgeWire.textReference({ ...saved, version: 3 }).version, 3);
  for (const value of [{ version: 2, catalog: "c", contractFingerprint: "f", key: "k" }, { ...saved, fallbackText: null },
    { ...saved, version: 0 }, { ...saved, version: 2.5 }])
    assert.throws(() => bridgeWire.textReference(value), JSON.stringify(value));
});

test("a later transport version decodes with the rest of the state and shows its fallback text", async () => {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  producer = undefined;
  const future = { ...saved, version: 3, fallbackText: "Saved (v3)." };
  bridge.view("status", { state: { status: future, count: 2 } });
  const client = await connectView<{ readonly status: TextReference; readonly count: number }>({
    contract: "Tests.Status:fp-3", route: "status", members: { status: bridgeWire.textReference, count: (value: unknown) => bridgeWire.integer(value, 0, 10) },
  });
  producer = bridge;
  cleanup.push(() => { client.dispose(); });
  assert.equal(client.snapshot.count, 2);
  // A runtime that does not read the version formats nothing, as resolveTextReference reports it.
  cleanup.push(configureTranslatedText({ ...translations(), resolve: reference => reference.version > 2 ? undefined : "translated" }));
  assert.equal(resolveText(client.snapshot.status), "Saved (v3).");
});

test("a push that repeats the window's locale after a reconnect restores a locale the page changed itself", async () => {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  producer = undefined;
  bridge.view("status", { state: { status: saved } });
  bridge.setWindowLocale("de");
  const runtime = translations();
  cleanup.push(configureTranslatedText(runtime));
  const client = await connectView<{ readonly status: TextReference }>({
    contract: "Tests.Status:fp-4", route: "status", members: { status: bridgeWire.textReference },
  });
  producer = bridge;
  cleanup.push(() => { client.dispose(); });
  await bridge.flush();
  assert.equal(runtime.getLocale(), "de");
  runtime.setLocale!("fr");
  assert.equal(runtime.getLocale(), "en", "the test runtime has no fr, so it shows en");
  const locales: (string | null)[] = [];
  cleanup.push(onWindowLocale(locale => { locales.push(locale); }));
  bridge.disconnect();
  bridge.reconnect();
  await bridge.flush();
  assert.equal(runtime.getLocale(), "de", "the replayed de was applied");
  assert.deepEqual(locales, [], "the window's locale did not change, so its listeners heard nothing");
});

test("number fields follow the window's locale, but not while the user edits a draft", async () => {
  await reportWindowLocale("de");
  type State = { readonly amount: number };
  let state: State = { amount: 1.5 };
  const written: number[] = [];
  const field = createFieldController<State, object, number>({
    name: "amount", kind: "number", read: current => current.amount,
    write: async (_client, value) => { written.push(value); state = { amount: value }; field.setSource({ state, client: {} }); },
  }, { commit: "pause" });
  field.setSource({ state, client: {} });
  const stop = field.subscribe(() => {});
  assert.equal(field.current.text, "1,5");
  field.inputText("2,5");
  await field.commit();
  assert.deepEqual(written, [2.5], "1,5-style text is read in the window's locale");
  // A draft keeps the locale it was typed in.
  field.inputText("3,");
  await reportWindowLocale("en");
  assert.equal(field.current.text, "3,");
  field.reset();
  assert.equal(field.current.text, "2.5", "without the draft, the text follows the window's locale");
  stop();
  field.dispose();
});

test("date and time fields follow the window's locale, but not while the user edits a draft (E5)", async () => {
  await reportWindowLocale("de");
  type State = { readonly due: string; readonly at: string };
  let state: State = { due: "2026-11-03", at: "21:05:00" };
  const written: string[] = [];
  const due = createFieldController<State, object, string>({
    name: "due", kind: "date", read: current => current.due,
    write: async (_client, value) => { written.push(value); state = { ...state, due: value }; due.setSource({ state, client: {} }); },
  }, { commit: "pause" });
  const at = createFieldController<State, object, string>({
    name: "at", kind: "time", read: current => current.at, write: async () => undefined,
  });
  due.setSource({ state, client: {} });
  at.setSource({ state, client: {} });
  const stops = [due.subscribe(() => {}), at.subscribe(() => {})];
  assert.equal(due.current.text, "03.11.2026");
  assert.equal(at.current.text, "21:05");
  due.inputText("24.12.2026");
  await due.commit();
  assert.deepEqual(written, ["2026-12-24"], "the wire value stays ISO");
  due.inputText("2026-12-25");
  await due.commit();
  assert.deepEqual(written, ["2026-12-24", "2026-12-25"], "the ISO form is read in any locale");
  assert.equal(due.current.text, "25.12.2026");
  // Not editing: a switch formats again.
  await reportWindowLocale("en-US");
  assert.equal(due.current.text, "12/25/2026");
  assert.equal(at.current.text, "9:05 PM");
  // Editing: the draft keeps the locale it was typed in.
  due.inputText("1/2/202");
  await reportWindowLocale("en-GB");
  assert.equal(due.current.text, "1/2/202");
  due.reset();
  assert.equal(due.current.text, "25/12/2026", "without the draft, the text follows the window's locale");
  await reportWindowLocale(null);
  assert.equal(due.current.text, "2026-12-25", "without a window locale, the ISO form");
  for (const stop of stops) stop();
  due.dispose();
  at.dispose();
});

test("a field with locale null, or bound to a native date or number input, keeps the ISO and invariant forms (E5)", async () => {
  await reportWindowLocale("de");
  type State = { readonly due: string; readonly amount: number };
  const state: State = { due: "2026-11-03", amount: 1.5 };
  const iso = createFieldController<State, object, string>({ kind: "date", read: current => current.due, write: async () => undefined }, { locale: null });
  iso.setSource({ state, client: {} });
  assert.equal(iso.current.text, "2026-11-03");
  const element = (type: string) => Object.assign(new EventTarget(), {
    tagName: "INPUT", type, value: "", setAttribute() {}, getAttribute: () => null,
  }) as unknown as HTMLInputElement;
  const client = {
    snapshot: state, dispose() {},
    subscribe(listener: (current: State) => void) { listener(state); return () => {}; },
  };
  const dateInput = element("date");
  const numberInput = element("number");
  const textInput = element("text");
  const bound = [
    bindField(dateInput, client, { kind: "date", read: current => current.due, write: async () => undefined }),
    bindField(numberInput, client, { kind: "number", read: current => current.amount, write: async () => undefined }),
    bindField(textInput, client, { kind: "date", read: current => current.due, write: async () => undefined }),
  ];
  assert.equal(dateInput.value, "2026-11-03", "<input type=date> takes the ISO form");
  assert.equal(numberInput.value, "1.5", "<input type=number> takes the invariant form");
  assert.equal(textInput.value, "03.11.2026", "a text input follows the window's locale");
  for (const field of bound) field.dispose();
  iso.dispose();
});
