import { describe, expect, it } from "vitest";
import {
  BridgeError, FieldError, configureTranslatedText, createFieldController, createRunicInstance, describeBridgeError, fieldErrorText, formatShortcut,
  runicText, runicTextKeys, type FieldErrorArguments, type FieldErrorReason, type RunicTextKey,
} from "@runic-artifex/views";
import {
  configureRunicViewsText, resolveRunicViewsText, runicViewsCatalog, runicViewsFieldErrorText, runicViewsMessages, runicViewsText, runicViewsTransport,
} from "../index.js";

const invalid = (reason: FieldErrorReason, args: FieldErrorArguments = {}) =>
  new FieldError("invalid-text", "English", undefined, { reason, arguments: args });

describe("runic-views field errors", () => {
  // The reason, its arguments, the catalog message that gives its text, and that text in English and German.
  const cases: [FieldErrorReason, FieldErrorArguments, string, string, string][] = [
    ["required", {}, "field_required_number", "Enter a number.", "Geben Sie eine Zahl ein."],
    ["required", { expected: "whole-number" }, "field_required_whole_number", "Enter a whole number.", "Geben Sie eine ganze Zahl ein."],
    ["required", { expected: "date" }, "field_required_date", "Enter a date.", "Geben Sie ein Datum ein."],
    ["required", { expected: "time" }, "field_required_time", "Enter a time.", "Geben Sie eine Uhrzeit ein."],
    ["required", { expected: "date-time" }, "field_required_date_time", "Enter a date and time.", "Geben Sie ein Datum mit Uhrzeit ein."],
    ["not-a-number", { text: "1e" }, "field_not_a_number", "1e is not a number.", "1e ist keine Zahl."],
    ["not-a-whole-number", { text: "1.5" }, "field_not_a_whole_number", "1.5 is not a whole number.", "1.5 ist keine ganze Zahl."],
    ["too-many-fraction-digits", { text: "1.234", scale: 2 }, "field_too_many_fraction_digits", "1.234 has more than 2 digits after the decimal separator.", "1.234 hat mehr als 2 Nachkommastellen."],
    ["too-many-fraction-digits", { text: "1.23", scale: 1 }, "field_too_many_fraction_digits", "1.23 has more than 1 digit after the decimal separator.", "1.23 hat mehr als 1 Nachkommastelle."],
    ["out-of-range", { text: "9e99" }, "field_out_of_range", "9e99 is too large.", "9e99 ist zu groß."],
    ["not-a-boolean", { text: "yes" }, "field_not_a_boolean", "yes is not true or false.", "yes ist weder wahr noch falsch."],
    ["not-a-choice", { text: "Huge", choices: ["Small", "Medium", "Large"] }, "field_not_a_choice", "Huge is not one of Small, Medium, or Large.", "Huge ist keiner der Werte Small, Medium oder Large."],
    ["not-a-date", { text: "31.02.2026" }, "field_not_a_date", "31.02.2026 is not a date.", "31.02.2026 ist kein Datum."],
    ["not-a-date", { text: "x", patterns: ["DD.MM.YYYY", "YYYY-MM-DD"] }, "field_not_a_date_in", "x is not a date (DD.MM.YYYY or YYYY-MM-DD).", "x ist kein Datum (DD.MM.YYYY oder YYYY-MM-DD)."],
    ["not-a-time", { text: "25:00" }, "field_not_a_time", "25:00 is not a time.", "25:00 ist keine Uhrzeit."],
    ["not-a-time", { text: "x", patterns: ["hh:mm", "hh:mm:ss"] }, "field_not_a_time_in", "x is not a time (hh:mm or hh:mm:ss).", "x ist keine Uhrzeit (hh:mm oder hh:mm:ss)."],
    ["not-a-date-time", { text: "x", patterns: ["YYYY-MM-DDThh:mm"] }, "field_not_a_date_time", "x is not a date and time (YYYY-MM-DDThh:mm).", "x ist kein Datum mit Uhrzeit (YYYY-MM-DDThh:mm)."],
    ["offset-out-of-range", { text: "+15:00" }, "field_offset_out_of_range", "+15:00 has an offset beyond ±14:00.", "+15:00 hat eine UTC-Abweichung über ±14:00."],
    ["no-text-form", {}, "field_no_text_form", "This field has no text form.", "Dieses Feld lässt sich nicht als Text eingeben."],
  ];

  it.each(cases)("%s %j", (reason, args, message, english, german) => {
    const error = invalid(reason, args);
    const format = (runicViewsMessages as unknown as Record<string, (inputs: unknown, options: unknown) => string>)[message]!;
    // The message's inputs: none for required, else the text and the arguments the reason has.
    const inputs = reason === "required" || reason === "no-text-form" ? undefined
      : { text: args.text, ...args.scale === undefined ? {} : { scale: args.scale }, ...args.choices === undefined ? {} : { choices: args.choices },
        ...args.patterns === undefined ? {} : { patterns: args.patterns } };
    expect(inputs === undefined ? format({ locale: "en" }, undefined) : format(inputs, { locale: "en" })).toBe(english);
    expect(runicViewsFieldErrorText(error, "en")).toBe(english);
    expect(runicViewsFieldErrorText(error, "de")).toBe(german);
    expect(runicViewsFieldErrorText(error, "de-AT")).toBe(german);
  });

  it("covers every reason the parsers report", () => {
    const reasons: FieldErrorReason[] = ["required", "not-a-number", "not-a-whole-number", "too-many-fraction-digits", "out-of-range",
      "not-a-boolean", "not-a-choice", "not-a-date", "not-a-time", "not-a-date-time", "offset-out-of-range", "no-text-form"];
    for (const reason of reasons) expect(runicViewsFieldErrorText(invalid(reason, { text: "x", patterns: ["p"] }), "de"), reason).toBeTypeOf("string");
  });

  // Key coverage: every field message of the catalog is the text of a reason, so none is left over.
  it("has a reason for every field message", () => {
    const used = new Set(cases.map(([, , message]) => message));
    expect(Object.keys(runicViewsMessages).filter(name => name.startsWith("field_")).sort()).toEqual([...used].sort());
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

describe("Runic's page text", () => {
  // German that is the English word.
  const sameInGerman = new Set<RunicTextKey>(["platform.alt_key", "shortcut.escape", "shortcut.tab"]);

  // Key coverage: every key the page writes has a message whose English is the page's own, and German.
  it.each(runicTextKeys.map(key => [key]))("%s", key => {
    const english = runicText(key);
    expect(runicViewsText(key, "en")).toBe(english);
    const german = runicViewsText(key, "de");
    expect(german).toBeTypeOf("string");
    expect(german!.trim().length).toBeGreaterThan(0);
    if (!sameInGerman.has(key)) expect(german).not.toBe(english);
    expect(runicViewsText(key, "de-AT")).toBe(german);
  });

  // ... and the catalog's page groups have no message the page does not write.
  it("has a key for every bridge and shortcut message", () => {
    const keys = new Set<string>(runicTextKeys.map(key => key.replace(".", "_")));
    for (const name of Object.keys(runicViewsMessages).filter(name => name.startsWith("bridge_") || name.startsWith("shortcut_")))
      expect(keys.has(name), name).toBe(true);
  });

  it("leaves keys it has no message for to the English text", () => {
    expect(runicViewsText("bridge.a_future_sentence" as RunicTextKey, "de")).toBeUndefined();
    expect(runicViewsText("toString" as RunicTextKey, "de")).toBeUndefined();
  });

  it("shows error sentences and shortcut labels in the page's language once configured", () => {
    let locale = "de";
    const stopText = configureTranslatedText({ resolve: () => undefined, getLocale: () => locale });
    const stop = configureRunicViewsText();
    try {
      expect(describeBridgeError(new BridgeError("disconnected", "x"))).toBe("Die Verbindung zur Anwendung wurde unterbrochen. Sie wird automatisch wiederhergestellt.");
      expect(describeBridgeError(new BridgeError("rejected", "Count has an invalid value.", { reason: "invalid-input" }))).toBe("Die Eingabe ist ungültig.");
      expect(describeBridgeError(invalid("not-a-number", { text: "1e" }))).toBe("1e ist keine Zahl.");
      expect(formatShortcut("Primary+Shift+PageDown", { apple: false })).toBe("Strg+Umschalt+Bild-ab");
      expect(formatShortcut("Alt+Delete", { apple: false })).toBe("Alt+Entf");
      expect(formatShortcut("Primary+Space", { apple: true })).toBe("⌘Leertaste");
      locale = "en";
      expect(formatShortcut("Primary+Shift+PageDown", { apple: false })).toBe("Ctrl+Shift+Page Down");
    } finally {
      stop();
      stopText();
    }
    expect(describeBridgeError(new BridgeError("disconnected", "x"))).toBe("The connection to the application was lost. It is restored automatically.");
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
      const stopGerman = a.run(() => configureTranslatedText({ resolve: () => undefined, getLocale: () => "de" }));
      expect(a.run(() => formatShortcut("Primary+Delete", { apple: false }))).toBe("Strg+Entf");
      expect(b.run(() => formatShortcut("Primary+Delete", { apple: false }))).toBe("Ctrl+Del");
      stopGerman();
    } finally {
      stop();
      a.dispose();
      b.dispose();
    }
  });
});
