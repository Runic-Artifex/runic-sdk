import assert from "node:assert/strict";
import { texts } from "./dist/page.js";

// The application's English override.
assert.equal(texts.en.untitled, "New document");
assert.equal(texts.en.saveFile, "Save file");
// Locale-first: German keeps the package's German where only the English is overridden.
assert.equal(texts.de.untitled, "Unbenannt");
assert.equal(texts.de.notAChoice, "XL ist keiner der Werte S, M oder L.");
// French, which the package lacks: the application's messages, and English for the rest.
assert.equal(texts.fr.untitled, "Sans titre");
assert.equal(texts.fr.notANumber, "1e n’est pas un nombre.");
assert.equal(texts.fr.saveFile, "Save file");
console.log("The views-translations Vite build with overrides passed.");
