// translation-text/1 against the real Runic Translations ESM: the state the .NET producer writes
// (fixtures/translations/producer-state.json, TranslatedTextTests.TheProducersReferencesAreThePageFixture) is decoded by
// the generated client's decoders and resolved with the generated `resolveTextReference` of a small catalog
// (fixtures/translations/catalog, generated with runic-translations 0.8.0 into fixtures/translations/generated).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { configureTranslatedText, resolveText } from "../dist/index.js";
import { decodeBridgeValidation } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { m } from "./fixtures/translations/generated/notes.esm-v5/messages.js";
import { configureLocaleResolver, createLocaleSource } from "./fixtures/translations/generated/notes.esm-v5/runtime.js";
import { resolveTextReference } from "./fixtures/translations/generated/notes.esm-v5/transport.js";

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
