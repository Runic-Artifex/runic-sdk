// Field errors an application can translate (S5a): the reason and arguments of `invalid-text` errors, their English
// message, and the application's text through `configureFieldErrorText`, `resolveText` and `describeBridgeError`.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import {
  configureFieldErrorText, configureTranslatedText, createFieldController, describeBridgeError, FieldError, fieldErrorText,
  resolveText, subscribeText, type BridgeField, type FieldErrorArguments, type FieldErrorReason,
} from "../dist/index.js";

type State = { readonly value: unknown };
const client = { snapshot: { value: undefined }, subscribe: () => () => {}, dispose() {} };

// The error of a text typed into a field of the given traits, or undefined when it parses.
function errorOf(text: string, definition: Partial<BridgeField<typeof client, State, unknown>>,
  options: Parameters<typeof createFieldController<State, typeof client, unknown>>[1] = {}): FieldError | undefined {
  const controller = createFieldController<State, typeof client, unknown>(
    { read: () => undefined, write: () => Promise.resolve(), ...definition }, { coalesce: false, commit: "pause", delay: 60_000, ...options });
  controller.setSource({ state: { value: undefined }, client });
  controller.inputText(text);
  const error = controller.current.error;
  controller.dispose();
  return error instanceof FieldError ? error : undefined;
}

const cleanup: (() => void)[] = [];
afterEach(() => { for (const step of cleanup.splice(0).reverse()) step(); });

test("each invalid text has a reason and arguments, and keeps its English message", () => {
  const cases: [string, Partial<BridgeField<typeof client, State, unknown>>, { locale?: string | null; scale?: number },
    FieldErrorReason, FieldErrorArguments, string][] = [
    ["", { kind: "number" }, {}, "required", { expected: "number" }, "Enter a number."],
    ["", { kind: "decimal" }, {}, "required", { expected: "number" }, "Enter a number."],
    ["", { kind: "bigint" }, {}, "required", { expected: "whole-number" }, "Enter a whole number."],
    ["", { kind: "date" }, {}, "required", { expected: "date" }, "Enter a date."],
    ["", { kind: "time" }, {}, "required", { expected: "time" }, "Enter a time."],
    ["", { kind: "datetime" }, {}, "required", { expected: "date-time" }, "Enter a date and time."],
    [" 1e ", { kind: "number" }, {}, "not-a-number", { text: "1e" }, "1e is not a number."],
    ["1.5", { kind: "number" }, { locale: "de-DE" }, "not-a-number", { text: "1.5" }, "1.5 is not a number."],
    ["abc", { kind: "decimal" }, {}, "not-a-number", { text: "abc" }, "abc is not a number."],
    ["1.5", { kind: "number", integer: true }, {}, "not-a-whole-number", { text: "1.5" }, "1.5 is not a whole number."],
    ["1.5", { kind: "bigint" }, {}, "not-a-whole-number", { text: "1.5" }, "1.5 is not a whole number."],
    ["1.25", { kind: "number" }, { scale: 1 }, "too-many-fraction-digits", { text: "1.25", scale: 1 },
      "1.25 has more than 1 digits after the decimal separator."],
    ["0.12345678901234567890123456789", { kind: "decimal" }, {}, "too-many-fraction-digits",
      { text: "0.12345678901234567890123456789", scale: 28 },
      "0.12345678901234567890123456789 has more than 28 digits after the decimal separator."],
    ["79228162514264337593543950336", { kind: "decimal" }, {}, "out-of-range", { text: "79228162514264337593543950336" },
      "79228162514264337593543950336 is too large."],
    ["yes", { kind: "boolean" }, {}, "not-a-boolean", { text: "yes" }, "yes is not true or false."],
    ["Large", { kind: "choice", choices: ["Small", "Medium"] }, {}, "not-a-choice", { text: "Large", choices: ["Small", "Medium"] },
      "Large is not one of Small, Medium."],
    ["soon", { kind: "date" }, {}, "not-a-date", { text: "soon", patterns: ["YYYY-MM-DD"] }, "soon is not a date (YYYY-MM-DD)."],
    ["2026-02-31", { kind: "date" }, {}, "not-a-date", { text: "2026-02-31" }, "2026-02-31 is not a date."],
    ["soon", { kind: "time" }, {}, "not-a-time", { text: "soon", patterns: ["hh:mm", "hh:mm:ss"] }, "soon is not a time (hh:mm or hh:mm:ss)."],
    ["soon", { kind: "datetime" }, {}, "not-a-date-time", { text: "soon", patterns: ["YYYY-MM-DDThh:mm"] },
      "soon is not a date and time (YYYY-MM-DDThh:mm)."],
    ["2026-11-04T25:00", { kind: "datetime" }, {}, "not-a-time", { text: "2026-11-04T25:00" }, "2026-11-04T25:00 is not a time."],
    ["2026-11-04T10:00+15:00", { kind: "datetime" }, {}, "offset-out-of-range", { text: "2026-11-04T10:00+15:00" },
      "2026-11-04T10:00+15:00 has an offset beyond ±14:00."],
    ["x", { kind: "value" }, {}, "no-text-form", {}, "This field has no text form."],
  ];
  for (const [text, definition, options, reason, args, message] of cases) {
    const error = errorOf(text, definition, options);
    const label = `${JSON.stringify(text)} in ${JSON.stringify(definition)}`;
    assert.ok(error, label);
    assert.equal(error.kind, "invalid-text", label);
    assert.equal(error.reason, reason, label);
    assert.deepEqual(error.arguments, args, label);
    assert.equal(error.message, message, label);
    assert.ok(Object.isFrozen(error.arguments), label);
  }
});

test("a locale's date pattern comes first in the patterns", () => {
  const error = errorOf("soon", { kind: "date" }, { locale: "de-DE" });
  assert.equal(error?.reason, "not-a-date");
  assert.deepEqual(error?.arguments.patterns, ["DD.MM.YYYY", "YYYY-MM-DD"]);
  assert.equal(error?.message, "soon is not a date (DD.MM.YYYY or YYYY-MM-DD).");
});

test("a field's own parse and the producer's receipts have no reason", () => {
  const own = errorOf("x", { kind: "text" }, { parse: () => { throw new Error("Use letters only."); } });
  assert.equal(own?.kind, "invalid-text");
  assert.equal(own?.reason, undefined);
  assert.deepEqual(own?.arguments, {});
  assert.equal(own?.message, "Use letters only.");
  const conflict = new FieldError("conflict", "The write baseline no longer matches the authoritative field.");
  assert.equal(conflict.reason, undefined);
  assert.deepEqual(conflict.arguments, {});
});

test("configureFieldErrorText gives field errors the application's text in the text locale", () => {
  const error = errorOf("1e", { kind: "number" })!;
  assert.equal(fieldErrorText(error), "1e is not a number.", "the English message without a hook");
  assert.equal(resolveText(error), "1e is not a number.");
  let changes = 0;
  cleanup.push(subscribeText(() => { changes++; }));
  const locales: (string | undefined)[] = [];
  const stop = configureFieldErrorText((shown, locale) => {
    locales.push(locale);
    return shown.reason === "not-a-number" ? `${shown.arguments.text} ist keine Zahl.` : undefined;
  });
  cleanup.push(stop);
  assert.equal(changes, 1, "configuring the text renders again");
  assert.equal(fieldErrorText(error), "1e ist keine Zahl.");
  assert.equal(resolveText(error), "1e ist keine Zahl.");
  assert.equal(describeBridgeError(error), "1e ist keine Zahl.");
  const other = errorOf("", { kind: "date" })!;
  assert.equal(resolveText(other), "Enter a date.", "undefined shows the message");
  // The locale is the one references are shown in.
  const stopTranslations = configureTranslatedText({ resolve: () => undefined, getLocale: () => "de" });
  cleanup.push(stopTranslations);
  fieldErrorText(error);
  assert.equal(locales.at(-1), "de");
  stop();
  assert.equal(resolveText(error), "1e is not a number.", "removing it shows the message again");
});

test("a later configureFieldErrorText replaces an earlier one, and a throwing hook shows the message", () => {
  const error = errorOf("1e", { kind: "number" })!;
  const first = configureFieldErrorText(() => "first");
  cleanup.push(first);
  const second = configureFieldErrorText(() => { throw new Error("broken"); });
  cleanup.push(second);
  const reported: unknown[] = [];
  const host = globalThis as { reportError?: (error: unknown) => void };
  const saved = host.reportError;
  host.reportError = failure => { reported.push(failure); };
  try {
    assert.equal(fieldErrorText(error), "1e is not a number.");
    assert.equal(reported.length, 1, "the failure is reported");
    first();
    assert.equal(resolveText(error), "1e is not a number.", "the replaced hook's stop leaves the later one");
    assert.equal(reported.length, 2);
  } finally { if (saved) host.reportError = saved; else delete host.reportError; }
  second();
  assert.equal(resolveText(error), "1e is not a number.");
  assert.throws(() => configureFieldErrorText(undefined as never), TypeError);
});
