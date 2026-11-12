import { describe, expect, it } from "vitest";
import {
  FieldError, configureTranslatedText, createFieldController, createRunicInstance, fieldErrorText, type FieldErrorArguments, type FieldErrorReason,
} from "@runic-artifex/views";
import {
  configureRunicViewsText, resolveRunicViewsText, runicViewsCatalog, runicViewsFieldErrorText, runicViewsMessages, runicViewsTransport,
} from "../index.js";

const invalid = (reason: FieldErrorReason, args: FieldErrorArguments = {}) =>
  new FieldError("invalid-text", "English", undefined, { reason, arguments: args });

describe("runic-views field errors", () => {
  const cases: [FieldErrorReason, FieldErrorArguments, string, string][] = [
    ["required", {}, "Enter a number.", "Geben Sie eine Zahl ein."],
    ["required", { expected: "whole-number" }, "Enter a whole number.", "Geben Sie eine ganze Zahl ein."],
    ["required", { expected: "date" }, "Enter a date.", "Geben Sie ein Datum ein."],
    ["required", { expected: "time" }, "Enter a time.", "Geben Sie eine Uhrzeit ein."],
    ["required", { expected: "date-time" }, "Enter a date and time.", "Geben Sie ein Datum mit Uhrzeit ein."],
    ["not-a-number", { text: "1e" }, "1e is not a number.", "1e ist keine Zahl."],
    ["not-a-whole-number", { text: "1.5" }, "1.5 is not a whole number.", "1.5 ist keine ganze Zahl."],
    ["too-many-fraction-digits", { text: "1.234", scale: 2 }, "1.234 has more than 2 digits after the decimal separator.", "1.234 hat mehr als 2 Nachkommastellen."],
    ["too-many-fraction-digits", { text: "1.23", scale: 1 }, "1.23 has more than 1 digit after the decimal separator.", "1.23 hat mehr als 1 Nachkommastelle."],
    ["out-of-range", { text: "9e99" }, "9e99 is too large.", "9e99 ist zu groß."],
    ["not-a-boolean", { text: "yes" }, "yes is not true or false.", "yes ist weder wahr noch falsch."],
    ["not-a-choice", { text: "Huge", choices: ["Small", "Medium", "Large"] }, "Huge is not one of Small, Medium, or Large.", "Huge ist keiner der Werte Small, Medium oder Large."],
    ["not-a-date", { text: "31.02.2026" }, "31.02.2026 is not a date.", "31.02.2026 ist kein Datum."],
    ["not-a-date", { text: "x", patterns: ["DD.MM.YYYY", "YYYY-MM-DD"] }, "x is not a date (DD.MM.YYYY or YYYY-MM-DD).", "x ist kein Datum (DD.MM.YYYY oder YYYY-MM-DD)."],
    ["not-a-time", { text: "25:00" }, "25:00 is not a time.", "25:00 ist keine Uhrzeit."],
    ["not-a-time", { text: "x", patterns: ["hh:mm", "hh:mm:ss"] }, "x is not a time (hh:mm or hh:mm:ss).", "x ist keine Uhrzeit (hh:mm oder hh:mm:ss)."],
    ["not-a-date-time", { text: "x", patterns: ["YYYY-MM-DDThh:mm"] }, "x is not a date and time (YYYY-MM-DDThh:mm).", "x ist kein Datum mit Uhrzeit (YYYY-MM-DDThh:mm)."],
    ["offset-out-of-range", { text: "+15:00" }, "+15:00 has an offset beyond ±14:00.", "+15:00 hat eine UTC-Abweichung über ±14:00."],
    ["no-text-form", {}, "This field has no text form.", "Dieses Feld lässt sich nicht als Text eingeben."],
  ];

  it.each(cases)("%s %j", (reason, args, english, german) => {
    const error = invalid(reason, args);
    expect(runicViewsFieldErrorText(error, "en")).toBe(english);
    expect(runicViewsFieldErrorText(error, "de")).toBe(german);
    expect(runicViewsFieldErrorText(error, "de-AT")).toBe(german);
  });

  it("covers every reason the parsers report", () => {
    const reasons: FieldErrorReason[] = ["required", "not-a-number", "not-a-whole-number", "too-many-fraction-digits", "out-of-range",
      "not-a-boolean", "not-a-choice", "not-a-date", "not-a-time", "not-a-date-time", "offset-out-of-range", "no-text-form"];
    for (const reason of reasons) expect(runicViewsFieldErrorText(invalid(reason, { text: "x", patterns: ["p"] }), "de"), reason).toBeTypeOf("string");
  });

  it("leaves other errors and unknown reasons to their message", () => {
    expect(runicViewsFieldErrorText(new FieldError("rejected", "No."), "de")).toBeUndefined();
    expect(runicViewsFieldErrorText(invalid("not-a-color" as FieldErrorReason, { text: "x" }), "de")).toBeUndefined();
    expect(runicViewsFieldErrorText(invalid("required", { expected: "colour" as never }), "de")).toBeUndefined();
  });

  it("shows field errors in the page's language once configured", () => {
    let locale = "de";
    const stopText = configureTranslatedText({ resolve: () => undefined, getLocale: () => locale });
    const stop = configureRunicViewsText();
    try {
      const error = invalid("not-a-number", { text: "1e" });
      expect(fieldErrorText(error)).toBe("1e ist keine Zahl.");
      locale = "en";
      expect(fieldErrorText(error)).toBe("1e is not a number.");
    } finally {
      stop();
      stopText();
    }
    expect(fieldErrorText(invalid("not-a-number", { text: "1e" }))).toBe("English");
  });

  // The errors Runic's own parsers report for a field's typed text.
  it("translates the errors of a number field", () => {
    const client = { snapshot: { value: undefined }, subscribe: () => () => {}, dispose() {} };
    const controller = createFieldController<{ readonly value: unknown }, typeof client, unknown>(
      { kind: "number", read: () => undefined, write: () => Promise.resolve() }, { coalesce: false, commit: "pause", delay: 60_000 });
    controller.setSource({ state: { value: undefined }, client });
    controller.inputText("1e");
    const error = controller.current.error;
    controller.dispose();
    expect(error).toBeInstanceOf(FieldError);
    expect(runicViewsFieldErrorText(error as FieldError, "de")).toBe("1e ist keine Zahl.");
  });
});

describe("runic-views references", () => {
  // The reference Runic.Application.Views.Translations writes for [Required] on a member named Name.
  const required = runicViewsTransport.createTextReference("validation_required", { field: "Name" }, { fallbackText: "Name is required." });

  it("resolves the validation texts of the NuGet package", () => {
    const reference = { ...required, catalog: runicViewsTransport.catalog, contractFingerprint: runicViewsTransport.contractFingerprint };
    expect(resolveRunicViewsText(reference, "de")).toBe("Name ist erforderlich.");
    expect(resolveRunicViewsText(reference, "en")).toBe("Name is required.");
    expect(resolveRunicViewsText({ ...reference, catalog: "app" }, "de")).toBeUndefined();
    expect(resolveRunicViewsText(null, "de")).toBeUndefined();
  });

  it("is an entry for createReferenceResolver", () => {
    const resolve = runicViewsTransport.createReferenceResolver([runicViewsCatalog]);
    const reference = { ...required, catalog: runicViewsTransport.catalog, contractFingerprint: runicViewsTransport.contractFingerprint };
    expect(resolve(reference, { locale: "de" }).text).toBe("Name ist erforderlich.");
    expect(runicViewsTransport.catalog).toBe("runic-views");
    expect(runicViewsMessages.platform_untitled_file_name({ locale: "de" })).toBe("Unbenannt");
  });
});

describe("Runic instances", () => {
  it("configures the field error text of one instance only", () => {
    const bridge = { isConnected: () => false, request: () => Promise.reject(new Error("unused")), onMessage: () => () => {},
      onConnectionChange: () => () => {} };
    const a = createRunicInstance({ bridge }), b = createRunicInstance({ bridge });
    const error = invalid("not-a-number", { text: "1e" });
    const stop = configureRunicViewsText(a);
    try {
      expect(a.run(() => fieldErrorText(error))).toBe("1e is not a number.");
      expect(b.run(() => fieldErrorText(error))).toBe("English");
      expect(fieldErrorText(error)).toBe("English");
    } finally {
      stop();
      a.dispose();
      b.dispose();
    }
  });
});
