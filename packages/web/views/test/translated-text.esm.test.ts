// translation-text/1 against the real Runic Translations ESM: the state the .NET producer writes
// (fixtures/translations/producer-state.json, TranslatedTextTests.TheProducersReferencesAreThePageFixture) is decoded by
// the generated client's decoders and resolved with the generated `resolveTextReference` of a small catalog
// (fixtures/translations/catalog, generated with runic-translations 0.9.1 into fixtures/translations/generated), and
// with `createReferenceResolver` beside a component library's catalog (fixtures/translations/library).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { configureTranslatedText, resolveText, syncDocumentLocale, textDirection } from "../dist/index.js";
import { decodeBridgeValidation } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { m } from "./fixtures/translations/generated/notes.esm-v5/messages.js";
import { configureLocaleResolver, createLocaleSource } from "./fixtures/translations/generated/notes.esm-v5/runtime.js";
import { resolveTextReference } from "./fixtures/translations/generated/notes.esm-v5/transport.js";
import * as notesTransport from "./fixtures/translations/generated/notes.esm-v5/transport.js";
import { m as grid } from "./fixtures/translations/generated/acme.grid.esm-v5/messages.js";
import {
  configureSharedLocaleResolver, localeDirection, removeBidiIsolates,
} from "./fixtures/translations/generated/acme.grid.esm-v5/runtime.js";
import * as gridTransport from "./fixtures/translations/generated/acme.grid.esm-v5/transport.js";

const state = JSON.parse(readFileSync(new URL("./fixtures/translations/producer-state.json", import.meta.url), "utf8")) as Record<string, unknown>;

test("the producer's references decode on the page and resolve with the generated runtime in both languages", () => {
  const saved = bridgeWire.textReference(state["saved"]);
  const changed = bridgeWire.textReference(state["changed"]);
  const failed = bridgeWire.textReference(state["failed"]);
  const future = bridgeWire.textReference(state["future"]);
  const validation = decodeBridgeValidation(state["validation"]);
  // List and nested message arguments arrive as the transport wrote them.
  assert.deepEqual(changed.arguments["packages"], { type: "list", value: ["firefox", "gimp"] });
  assert.deepEqual(failed.arguments["step"], {
    type: "message", value: { key: "steps_build", arguments: { name: { type: "string", value: "gimp" } }, fallbackText: "Building gimp" },
  });
  assert.equal(future.version, 3, "a later version decodes, and the rest of the state with it");
  const error = validation.errors.find(candidate => candidate.path[0] === "title")!;
  assert.ok(error.text);

  const locale = createLocaleSource({ initialLocale: "en" });
  const stopResolver = configureLocaleResolver(locale.getLocale);
  const stop = configureTranslatedText({
    ...locale,
    resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
  });
  try {
    assert.equal(resolveText(saved), "Saved Atlas.");
    assert.equal(resolveText(changed), "Changed firefox and gimp");
    assert.equal(resolveText(failed), "Building gimp failed");
    assert.equal(resolveText(error.text, error.message), "Title must be at most 8 characters.");
    // The runtime reports the version it does not read; the page shows the fallback text.
    const unread = resolveTextReference(future, m, { locale: "en" });
    assert.equal(unread.outcome, "failed");
    assert.equal(resolveText(future), "Saved Atlas (v3).");
    locale.setLocale("de");
    assert.equal(resolveText(saved), "Atlas gespeichert.");
    assert.equal(resolveText(changed), "Geändert: firefox und gimp");
    assert.equal(resolveText(failed), "Baue gimp fehlgeschlagen");
    assert.equal(resolveText(error.text, error.message), "Der Titel darf höchstens 8 Zeichen lang sein.");
    assert.equal(resolveText(future), "Saved Atlas (v3).");
  } finally {
    stop();
    stopResolver();
  }
});

test("references of an application's and a library's catalog resolve by catalog, and the Arabic catalog isolates its placeholders", () => {
  const saved = bridgeWire.textReference(state["saved"]);
  const selection = bridgeWire.textReference(state["selection"]);
  // The application's language, which every catalog on the page follows through the shared locale.
  let current = "en";
  const listeners = new Set<(locale: string) => void>();
  const stopShared = configureSharedLocaleResolver(() => current);
  const resolveReference = notesTransport.createReferenceResolver([
    { transport: notesTransport, messages: m },
    { transport: gridTransport, messages: grid },
  ]);
  const stop = configureTranslatedText({
    resolve: (reference, language) => resolveReference(reference, { locale: language }).text,
    getLocale: () => current,
    setLocale(next) { current = next; for (const listener of [...listeners]) listener(next); },
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    direction: localeDirection,
  });
  const root = { attributes: new Map<string, string>(),
    getAttribute(name: string) { return this.attributes.get(name) ?? null; },
    setAttribute(name: string, value: string) { this.attributes.set(name, value); } };
  const stopDocument = syncDocumentLocale({ element: root as unknown as Element });
  try {
    assert.equal(resolveText(saved), "Saved Atlas.");
    assert.equal(resolveText(selection), "3 rows selected in Orders");
    // A reference of a catalog the page does not list shows its fallback text.
    assert.equal(resolveText({ ...selection, catalog: "acme.charts" }), "3 rows selected in Orders");
    // The library's messages follow the shared locale without a resolver of their own.
    assert.equal(grid.rows_selected({ count: 2, table: "Kunden" }), "2 rows selected in Kunden");

    current = "de";
    for (const listener of [...listeners]) listener(current);
    assert.equal(resolveText(saved), "Atlas gespeichert.");
    assert.equal(resolveText(selection), "3 Zeilen in Orders ausgewählt");
    assert.equal(grid.rows_selected({ count: 2, table: "Kunden" }), "2 Zeilen in Kunden ausgewählt");
    // A reference that another release of the library's catalog wrote is checked key by key: its key and arguments
    // still match, so it translates and only reports the changed contract; a key this release no longer has, or an
    // argument of another type, shows the reference's fallback text.
    const otherRelease = { ...selection, contractFingerprint: `sha256:${"0".repeat(64)}` };
    const skewed = resolveReference(otherRelease, { locale: "de" });
    assert.equal(skewed.outcome, "translated");
    assert.equal(skewed.text, "3 Zeilen in Orders ausgewählt");
    assert.deepEqual(skewed.diagnostics.map(diagnostic => diagnostic.reason), ["contract-changed"]);
    assert.equal(resolveText(otherRelease), "3 Zeilen in Orders ausgewählt");
    const removed = resolveReference({ ...otherRelease, key: "rows_removed" }, { locale: "de" });
    assert.equal(removed.outcome, "fallback");
    assert.equal(removed.text, selection.fallbackText);
    const retyped = resolveReference({ ...otherRelease, arguments: { ...otherRelease.arguments, table: { type: "boolean", value: true } } }, { locale: "de" });
    assert.equal(retyped.outcome, "fallback");
    assert.equal(retyped.text, selection.fallbackText);
    assert.equal(root.getAttribute("lang"), "de");
    assert.equal(root.getAttribute("dir"), "ltr");

    // Arabic: the library declares it, the application's catalog does not and shows English. A catalog that declares a
    // right-to-left locale isolates placeholders (FSI/LRI/RLI … PDI) in its formatted text since Runic Translations 0.9.
    current = "ar";
    for (const listener of [...listeners]) listener(current);
    const arabic = resolveText(selection);
    // RLI 3 PDI for the number, FSI Orders PDI for the string.
    assert.equal(arabic, "تم تحديد \u20673\u2069 صفوف في \u2068Orders\u2069");
    assert.equal(removeBidiIsolates(arabic), "تم تحديد 3 صفوف في Orders");
    // The application's English fallback for an Arabic request is isolated too.
    assert.equal(resolveText(saved), "Saved \u2068Atlas\u2069.");
    assert.equal(textDirection(), "rtl");
    assert.equal(root.getAttribute("lang"), "ar");
    assert.equal(root.getAttribute("dir"), "rtl");
  } finally {
    stopDocument();
    stop();
    stopShared();
  }
});
