// Runic Inspect's translation editor (#957): the panel's state machine against the replies of Runic Translations'
// in-page editing API (runic-translations-sdk#182): read, edit, switch locale, save, 422 diagnostics, 409 conflict with
// reapply or discard, 403 and failures.
import assert from "node:assert/strict";
import test from "node:test";
import {
  canSave, describeStructure, draftText, editReducer, hasEdits, isDirty, parseEditMessage, readText, saveRequest, startEdit,
} from "../dist/inspect-edit.js";
import { messageApiUrl } from "../dist/inspect-editor.js";

const hashEn = `sha256:${"a".repeat(64)}`, hashDe = `sha256:${"d".repeat(64)}`, hashEn2 = `sha256:${"b".repeat(64)}`;

function message({ en = "Counter", enHash = hashEn, de = "Zähler", deEditable = true } = {}) {
  return {
    catalog: "app", kind: "catalog", key: "heading", logicalKey: "heading", baseLocale: "en", content: "inline", structured: false,
    inputs: [], markup: [],
    locales: [
      { locale: "en", origin: "catalog", text: en, source: { path: "/app/translations/en.rmf2", line: 4, column: 1 },
        file: { path: "/app/translations/en.rmf2", exists: true, hash: enHash }, editable: true,
        structure: { supported: true, inputs: [], selectors: [], selectorFunctions: [], variants: [{ id: "0", keys: [], pattern: en }], pluralCategories: [] } },
      { locale: "de", fallback: "en", origin: "catalog", text: de, source: { path: "/app/translations/de.rmf2", line: 1, column: 1 },
        file: { path: "/app/translations/de.rmf2", exists: true, hash: hashDe }, editable: deEditable,
        ...(deEditable ? {} : { reason: "de has no source file beside en's." }) },
    ],
  };
}

const read = (state, body = { message: message() }, status = 200) => editReducer(state, { type: "read", reply: { status, body } });

test("read: the page's locale, every locale's text and hash; editing keeps a draft per locale", () => {
  let state = startEdit({ catalog: "app", key: "heading", locale: "de" });
  assert.equal(state.status, "loading");
  assert.equal(canSave(state), false);
  state = read(state);
  assert.equal(state.status, "ready");
  assert.equal(state.locale, "de", "the locale the page shows");
  assert.deepEqual(state.hashes, { en: hashEn, de: hashDe });
  assert.equal(draftText(state), "Zähler");
  assert.equal(canSave(state), false, "nothing edited");

  state = editReducer(state, { type: "input", text: "Zählwerk" });
  assert.equal(isDirty(state), true);
  assert.equal(canSave(state), true);
  state = editReducer(state, { type: "locale", locale: "en" });
  assert.equal(draftText(state), "Counter");
  assert.equal(hasEdits(state), true, "the German draft stays");
  state = editReducer(state, { type: "locale", locale: "de" });
  assert.equal(draftText(state), "Zählwerk");
  assert.deepEqual(saveRequest(state), { catalog: "app", key: "heading", locale: "de", text: "Zählwerk", expectedHash: hashDe });
  assert.equal(editReducer(state, { type: "locale", locale: "fr" }), state, "an unknown locale is ignored");
  state = editReducer(state, { type: "revert" });
  assert.equal(draftText(state), "Zähler");
  assert.equal(hasEdits(state), false);

  // A message without the page's locale opens on the base locale.
  assert.equal(read(startEdit({ catalog: "app", key: "heading", locale: "fr" })).locale, "en");
  assert.equal(read(startEdit({ catalog: "app", key: "heading" })).locale, "en");
});

test("save: 200 takes the written message and its new hash; the page shows it in place", () => {
  let state = editReducer(read(startEdit({ catalog: "app", key: "heading", locale: "en" })), { type: "input", text: "Clicks" });
  state = editReducer(state, { type: "save" });
  assert.equal(state.status, "saving");
  assert.equal(canSave(state), false);
  assert.equal(editReducer(state, { type: "input", text: "x" }), state, "no typing while saving");
  assert.equal(editReducer(state, { type: "save" }), state, "no second save");
  state = editReducer(state, { type: "saved", reply: { status: 200, body: { written: ["/app/translations/en.rmf2"], message: message({ en: "Clicks", enHash: hashEn2 }) } } });
  assert.equal(state.status, "saved");
  assert.deepEqual(state.written, ["/app/translations/en.rmf2"]);
  assert.equal(readText(state), "Clicks");
  assert.equal(state.hashes.en, hashEn2, "the next write names the new hash");
  assert.equal(isDirty(state), false);
  // Typing again goes back to editing.
  state = editReducer(state, { type: "input", text: "Clicks!" });
  assert.equal(state.status, "ready");
  assert.equal(state.written, undefined);
  assert.equal(saveRequest(state).expectedHash, hashEn2);
});

test("save: 422 shows the diagnostics and keeps the text; unsupported says why", () => {
  let state = editReducer(read(startEdit({ catalog: "app", key: "heading", locale: "en" })), { type: "input", text: "Count {" });
  state = editReducer(editReducer(state, { type: "save" }), { type: "saved", reply: { status: 422, body: {
    error: "invalid", detail: "The text does not compile.",
    diagnostics: [{ code: "RTR0012", severity: "error", message: "Unclosed placeholder.", path: "/app/translations/en.rmf2", line: 4, column: 17 }, { bogus: true }],
  } } });
  assert.equal(state.status, "invalid");
  assert.deepEqual(state.diagnostics, [{ code: "RTR0012", severity: "error", message: "Unclosed placeholder.", path: "/app/translations/en.rmf2", line: 4, column: 17 }]);
  assert.equal(draftText(state), "Count {", "the text stays for fixing");
  assert.equal(state.hashes.en, hashEn, "nothing was written");
  assert.equal(canSave(state), true, "save again after a fix");
  state = editReducer(state, { type: "input", text: "Count {$n}" });
  assert.equal(state.status, "ready");
  assert.equal(state.diagnostics.length, 1, "the diagnostics stay visible while fixing the text");
  assert.deepEqual(editReducer(state, { type: "save" }).diagnostics, [], "and are cleared by the next save");

  const unsupported = editReducer(editReducer(state, { type: "save" }), { type: "saved", reply: { status: 422, body: { error: "unsupported", diagnostics: [] } } });
  assert.equal(unsupported.status, "invalid");
  assert.equal(unsupported.detail, "This locale cannot be written.");
});

test("save: 409 shows the file's text; reapply keeps the edit over the new hash, discard takes the file's text", () => {
  let state = editReducer(read(startEdit({ catalog: "app", key: "heading", locale: "en" })), { type: "input", text: "Mine" });
  state = editReducer(editReducer(state, { type: "save" }), { type: "saved", reply: { status: 409, body: {
    error: "conflict", detail: "en.rmf2 changed since it was read.", message: message({ en: "Theirs", enHash: hashEn2 }),
  } } });
  assert.equal(state.status, "conflict");
  assert.deepEqual({ text: state.conflict.text, hash: state.conflict.hash, locale: state.conflict.locale }, { text: "Theirs", hash: hashEn2, locale: "en" });
  assert.equal(state.detail, "en.rmf2 changed since it was read.");
  assert.equal(canSave(state), false, "decide first");
  assert.equal(editReducer(state, { type: "refreshed", reply: { status: 200, body: { message: message() } } }), state, "a refresh waits for the decision");

  const reapplied = editReducer(state, { type: "reapply" });
  assert.equal(reapplied.status, "ready");
  assert.equal(draftText(reapplied), "Mine");
  assert.equal(readText(reapplied), "Theirs");
  assert.deepEqual(saveRequest(reapplied), { catalog: "app", key: "heading", locale: "en", text: "Mine", expectedHash: hashEn2 });
  const saved = editReducer(editReducer(reapplied, { type: "save" }), { type: "saved", reply: { status: 200, body: { written: [], message: message({ en: "Mine", enHash: hashEn }) } } });
  assert.equal(saved.status, "saved");

  const discarded = editReducer(state, { type: "discard" });
  assert.equal(discarded.status, "ready");
  assert.equal(draftText(discarded), "Theirs");
  assert.equal(isDirty(discarded), false);
  assert.equal(discarded.hashes.en, hashEn2);
  assert.equal(discarded.conflict, undefined);

  // A conflict without the current message cannot be resolved here.
  const lost = editReducer(editReducer(editReducer(read(startEdit({ catalog: "app", key: "heading" })), { type: "input", text: "x" }), { type: "save" }),
    { type: "saved", reply: { status: 409, body: { error: "conflict" } } });
  assert.equal(lost.status, "failed");
});

test("403, 404, 501 and a failed request say why; a refresh keeps edits and takes new hashes", () => {
  const forbidden = read(startEdit({ catalog: "app", key: "heading" }), { error: "forbidden", detail: "In-page translation editing accepts requests from loopback only." }, 403);
  assert.equal(forbidden.status, "forbidden");
  assert.match(forbidden.detail, /loopback only/u);
  const forbiddenSave = editReducer(editReducer(editReducer(read(startEdit({ catalog: "app", key: "heading" })), { type: "input", text: "x" }), { type: "save" }),
    { type: "saved", reply: { status: 403, body: { error: "forbidden" } } });
  assert.equal(forbiddenSave.status, "forbidden");
  assert.equal(forbiddenSave.detail, "The Translations dev server refused this client.");
  assert.match(read(startEdit({ catalog: "app", key: "heading" }), undefined, 404).detail, /no editing API/u);
  assert.match(read(startEdit({ catalog: "app", key: "heading" }), undefined, 501).detail, /project mode/u);
  assert.match(read(startEdit({ catalog: "app", key: "heading" }), undefined, 0).detail, /did not reach/u);
  assert.equal(read(startEdit({ catalog: "app", key: "heading" }), { message: { catalog: "app" } }).status, "failed", "a malformed reply");

  // The file changed elsewhere (an editor saved it): the German draft stays, English takes the new text and hash.
  let state = editReducer(read(startEdit({ catalog: "app", key: "heading", locale: "de" })), { type: "input", text: "Zählwerk" });
  state = editReducer(state, { type: "refreshed", reply: { status: 200, body: { message: message({ en: "Counter!", enHash: hashEn2 }) } } });
  assert.equal(state.status, "ready");
  assert.equal(draftText(state, "de"), "Zählwerk");
  assert.equal(draftText(state, "en"), "Counter!");
  assert.equal(state.hashes.en, hashEn2);
  assert.equal(editReducer(state, { type: "refreshed", reply: { status: 500 } }), state, "a failed refresh changes nothing");

  // A locale that cannot be written: no Save.
  const locked = editReducer(editReducer(read(startEdit({ catalog: "app", key: "heading", locale: "de" }), { message: message({ deEditable: false }) }), { type: "input", text: "x" }), { type: "save" });
  assert.equal(locked.status, "ready");
  assert.equal(canSave(locked), false);
});

test("the structure of plural, selector and markup messages, and the API's URL below base", () => {
  const plural = parseEditMessage({
    catalog: "app", key: "clicks", baseLocale: "en", structured: true, inputs: [{ name: "count", type: "int64" }], markup: ["strong"],
    locales: [{
      locale: "de", origin: "catalog", text: ".input {$count :integer}\n.match $count\none {{Ein Klick}}\n* {{{$count} Klicks}}",
      file: { path: "/app/translations/de.rmf2", exists: true, hash: hashDe }, editable: true,
      structure: { supported: true, inputs: [{ name: "count", type: "int64", function: "integer", declared: true }], selectors: ["count"],
        selectorFunctions: ["plural"], variants: [{ id: "0", keys: ["one"], pattern: "Ein Klick" }, { id: "1", keys: ["*"], pattern: "{$count} Klicks" }],
        pluralCategories: ["one", "other"] },
    }, { locale: 7 }, null],
  });
  assert.equal(plural.locales.length, 1, "malformed locales are dropped");
  assert.deepEqual(describeStructure(plural, plural.locales[0]), [
    "Inputs: $count :integer",
    "Selects on $count (plural): one · *",
    "Plural categories of de: one, other",
    "Markup: {#strong}",
  ]);
  const few = { ...plural.locales[0], locale: "pl", structure: { ...plural.locales[0].structure,
    variants: [{ id: "0", keys: ["one"], pattern: "x" }, { id: "1", keys: ["other"], pattern: "y" }], pluralCategories: ["one", "few", "many", "other"] } };
  assert.equal(describeStructure(plural, few)[2], "Plural categories of pl: one, few, many, other (no variant for few, many)");
  assert.deepEqual(describeStructure(parseEditMessage(message()), parseEditMessage(message()).locales[0]), []);

  assert.equal(messageApiUrl("/", { catalog: "app", key: "shop.cart" }), "/__runic-translations/message?key=shop.cart&catalog=app");
  assert.equal(messageApiUrl("/sub", { catalog: "acme.grid", key: "a b" }), "/sub/__runic-translations/message?key=a%20b&catalog=acme.grid");
  assert.equal(messageApiUrl("http://localhost:5173/app/"), "/app/__runic-translations/message");
});
