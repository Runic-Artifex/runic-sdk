import assert from "node:assert/strict";
import { test } from "node:test";
import {
  BridgeError, BridgeOperationUncertainError, FieldError, configureRunicText, configureTranslatedText, describeBridgeError, formatShortcut, isBridgeError,
  runicText, runicTextKeys,
} from "../dist/index.js";

test("isBridgeError tells Bridge errors and narrows them by kind", () => {
  const released = new BridgeError("released", "This View was released.");
  assert.equal(isBridgeError(released), true);
  assert.equal(isBridgeError(released, "released"), true);
  assert.equal(isBridgeError(released, "disconnected"), false);
  assert.equal(isBridgeError(released, ["disconnected", "released"]), true);
  assert.equal(isBridgeError(new Error("x")), false);
  assert.equal(isBridgeError(undefined), false);
  // Another copy of the package brands its errors with the same registered symbol.
  const foreign = Object.assign(new Error("x"), { kind: "timeout" });
  Object.defineProperty(foreign, Symbol.for("runic.views.BridgeError"), { value: true });
  assert.equal(isBridgeError(foreign, "timeout"), true);
  const error: unknown = released;
  if (isBridgeError(error, "released")) {
    const kind: "released" = error.kind;
    void kind;
  }
});

test("describeBridgeError gives Runic's sentence for a Bridge error, and the application's text for a field error", () => {
  assert.equal(describeBridgeError(undefined), "");
  assert.equal(describeBridgeError(null), "");
  assert.match(describeBridgeError(new BridgeError("unavailable", "No __runicBridge.")), /not running in a Runic host/);
  assert.match(describeBridgeError(new BridgeError("timeout", "Timed out after 5000 ms.")), /did not answer in time/);
  assert.match(describeBridgeError(new BridgeError("disconnected", "The Bridge is disconnected.")), /connection to the application was lost/);
  assert.match(describeBridgeError(new BridgeError("released", "This View was released.")), /closed or replaced/);
  assert.match(describeBridgeError(new BridgeError("cancelled", "")), /cancelled/);
  // The producer's message is diagnostic text, such as "Count has an invalid value.": never shown.
  assert.equal(describeBridgeError(new BridgeError("rejected", "Step has an invalid value.", { reason: "invalid-input" })), "The input is not valid.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "", { reason: "invalid-input" })), "The input is not valid.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "Save is unavailable.", { reason: "unavailable" })), "This action is not available right now.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "Open has no object for this key.", { reason: "unresolved-key" })), "The item no longer exists.");
  assert.match(describeBridgeError(new BridgeError("rejected", "Too many.", { reason: "capacity" })), /busy/);
  assert.match(describeBridgeError(new BridgeError("rejected", "Too many.", { reason: "busy" })), /busy/);
  assert.equal(describeBridgeError(new BridgeError("rejected", "Not a request.", { reason: "invalid-request" })), "The application rejected the request.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "x", { reason: "a-future-reason" })), "The application rejected the request.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "x", { reason: "toString" })), "The application rejected the request.");
  assert.match(describeBridgeError(new BridgeError("failed", "x", { reason: "protocol-mismatch" })), /different Runic versions/);
  assert.match(describeBridgeError(new BridgeError("failed", "Saved.", { reason: "state-withheld" })), /cannot show it yet/);
  assert.equal(describeBridgeError(new BridgeError("failed", "Save failed.", { detail: { message: "at C:\\secret" } })), "The request failed.");
  // A kind of another copy of the package that this one does not know.
  assert.equal(describeBridgeError(Object.assign(new BridgeError("failed", "x"), { kind: "exploded" })), "The request failed.");
  assert.equal(describeBridgeError(new FieldError("conflict", "Someone else changed the title.")), "Someone else changed the title.");
  assert.equal(describeBridgeError(new FieldError("rejected", "  ")), "The value was not saved.");
  assert.match(describeBridgeError(new BridgeOperationUncertainError("lost", { contract: "c", requestId: "r" })), /not known whether/);
  assert.equal(describeBridgeError(new TypeError("Bad value.")), "Bad value.");
  assert.equal(describeBridgeError("offline"), "offline");
});

test("configureRunicText gives describeBridgeError and formatShortcut the application's text in the text locale", () => {
  let locale = "de";
  const stopTranslations = configureTranslatedText({ resolve: () => undefined, getLocale: () => locale });
  const seen: unknown[] = [];
  const stop = configureRunicText((key, at) => {
    seen.push([key, at]);
    if (key === "bridge.failed") return at === "de" ? "Die Anfrage ist fehlgeschlagen." : undefined;
    if (key === "platform.control_key") return "Strg";
    if (key === "shortcut.delete") return "Entf";
    if (key === "shortcut.space") return "Leertaste";
    return undefined;
  });
  try {
    assert.equal(describeBridgeError(new BridgeError("failed", "Save failed.")), "Die Anfrage ist fehlgeschlagen.");
    assert.deepEqual(seen.at(-1), ["bridge.failed", "de"]);
    assert.equal(formatShortcut("Primary+Shift+Delete", { apple: false }), "Strg+Shift+Entf");
    assert.equal(formatShortcut("Primary+Space", { apple: true }), "⌘Leertaste");
    assert.equal(formatShortcut("Primary+Delete", { apple: true }), "⌘⌦");
    // undefined keeps Runic's English text.
    assert.match(describeBridgeError(new BridgeError("timeout", "x")), /did not answer in time/);
    locale = "en";
    assert.equal(describeBridgeError(new BridgeError("failed", "Save failed.")), "The request failed.");
  } finally {
    stop();
    stopTranslations();
  }
  assert.equal(formatShortcut("Primary+Shift+Delete", { apple: false }), "Ctrl+Shift+Del");
});

test("a throwing Runic text hook is reported once per key and locale, and the English text is shown", () => {
  const reported: unknown[] = [];
  const previous = globalThis.reportError;
  globalThis.reportError = error => { reported.push(error); };
  const stop = configureRunicText(() => { throw new Error("broken hook"); });
  try {
    assert.equal(runicText("shortcut.escape"), "Esc");
    assert.equal(runicText("shortcut.escape"), "Esc");
    assert.equal(runicText("bridge.failed"), "The request failed.");
    assert.equal(reported.length, 2);
  } finally {
    stop();
    globalThis.reportError = previous;
  }
  assert.throws(() => configureRunicText("text" as never), TypeError);
});

test("Runic's text keys are the runic-views messages the page shows", () => {
  assert.ok(Object.isFrozen(runicTextKeys));
  for (const key of runicTextKeys) {
    assert.match(key, /^(bridge|shortcut|platform)\.[a-z_]+$/);
    assert.ok(runicText(key).length > 0, key);
  }
});
