// The shortcut cases shared with Runic.Platform's DesktopShortcut (specs/desktop/fixtures/shortcuts.json).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { formatShortcut, matchesShortcut, parseShortcut, shortcutFromEvent, shortcutText, type ShortcutEvent } from "../dist/index.js";

type Cases = {
  parse: { text: string; canonical: string; apple: string; other: string }[];
  invalid: string[];
  match: { shortcut: string; apple: boolean; event: ShortcutEvent; matches: boolean }[];
  record: { apple: boolean; event: ShortcutEvent; shortcut: string | null }[];
};
const cases = JSON.parse(readFileSync(new URL("../../../../specs/desktop/fixtures/shortcuts.json", import.meta.url), "utf8")) as Cases;

test("shortcut text parses and displays as DesktopShortcut does", () => {
  for (const item of cases.parse) {
    const parsed = parseShortcut(item.text);
    assert.ok(parsed, item.text);
    assert.equal(shortcutText(parsed), item.canonical, item.text);
    assert.equal(formatShortcut(item.text, { apple: true }), item.apple, item.text);
    assert.equal(formatShortcut(item.text, { apple: false }), item.other, item.text);
  }
});

test("invalid shortcut text is rejected", () => {
  for (const text of cases.invalid) {
    assert.equal(parseShortcut(text), null, JSON.stringify(text));
    assert.equal(formatShortcut(text), "");
    assert.equal(matchesShortcut({ key: "s" }, text), false);
  }
});

test("key events match with Primary as Command on Apple platforms and Ctrl elsewhere", () => {
  for (const item of cases.match) {
    assert.equal(matchesShortcut(item.event, item.shortcut, { apple: item.apple }), item.matches,
      `${item.shortcut} (${item.apple ? "Apple" : "other"}) ${JSON.stringify(item.event)}`);
  }
});

test("a key press records the shortcut text DesktopShortcut reads, which matches the press", () => {
  for (const item of cases.record) {
    const what = `${item.apple ? "Apple" : "other"} ${JSON.stringify(item.event)}`;
    const recorded = shortcutFromEvent(item.event, { apple: item.apple });
    assert.equal(recorded, item.shortcut, what);
    if (recorded === null) continue;
    const parsed = parseShortcut(recorded);
    assert.ok(parsed, what);
    assert.equal(shortcutText(parsed), recorded, `${what} is canonical`);
    assert.equal(matchesShortcut(item.event, recorded, { apple: item.apple }), true, `${what} matches its recording`);
  }
});
