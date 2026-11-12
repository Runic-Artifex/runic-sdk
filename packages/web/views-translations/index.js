// Runic's own page text from the runic-views catalog (English and German): field errors, the sentences of
// describeBridgeError and the key names of formatShortcut, and the references of Runic.Application.Views.Translations'
// validation texts. The catalog is imported by a relative path, so an application
// whose Vite build uses runicTranslations({ dependencies: "auto" }) gets it regenerated with its overrides and locales.
import { configureFieldErrorText, configureRunicText } from "@runic-artifex/views";
import { m } from "./dist/translations/runic-views.esm-v5/messages.js";
import * as transport from "./dist/translations/runic-views.esm-v5/transport.js";

export { m as runicViewsMessages, transport as runicViewsTransport };

/** The catalog as an entry of a generated transport's `createReferenceResolver`. */
export const runicViewsCatalog = Object.freeze({ transport, messages: m });

/** Formats a `runic-views` reference in `locale`; `undefined` for a reference of another catalog or one it cannot format. */
export function resolveRunicViewsText(reference, locale) {
  if (typeof reference !== "object" || reference === null || reference.catalog !== transport.catalog) return undefined;
  const resolution = transport.resolveTextReference(reference, m, locale === undefined ? undefined : { locale });
  return resolution.outcome === "translated" ? resolution.text : undefined;
}

// The catalog joins lists in the message's locale (:runic:list type=disjunction).
const list = items => [...(items ?? [])].map(String);

/** The text of a field error in `locale`, as `configureFieldErrorText` takes it; `undefined` for a reason it has none for. */
export function runicViewsFieldErrorText(error, locale) {
  const options = locale === undefined ? undefined : { locale };
  const args = error.arguments ?? {};
  const text = args.text ?? "";
  switch (error.reason) {
    case "required":
      switch (args.expected ?? "number") {
        case "number": return m.field_required_number(options);
        case "whole-number": return m.field_required_whole_number(options);
        case "date": return m.field_required_date(options);
        case "time": return m.field_required_time(options);
        case "date-time": return m.field_required_date_time(options);
        default: return undefined;
      }
    case "not-a-number": return m.field_not_a_number({ text }, options);
    case "not-a-whole-number": return m.field_not_a_whole_number({ text }, options);
    case "too-many-fraction-digits": return m.field_too_many_fraction_digits({ text, scale: args.scale ?? 0 }, options);
    case "out-of-range": return m.field_out_of_range({ text }, options);
    case "not-a-boolean": return m.field_not_a_boolean({ text }, options);
    case "not-a-choice": return m.field_not_a_choice({ text, choices: list(args.choices) }, options);
    case "not-a-date":
      return args.patterns?.length ? m.field_not_a_date_in({ text, patterns: list(args.patterns) }, options)
        : m.field_not_a_date({ text }, options);
    case "not-a-time":
      return args.patterns?.length ? m.field_not_a_time_in({ text, patterns: list(args.patterns) }, options)
        : m.field_not_a_time({ text }, options);
    case "not-a-date-time": return m.field_not_a_date_time({ text, patterns: list(args.patterns) }, options);
    case "offset-out-of-range": return m.field_offset_out_of_range({ text }, options);
    case "no-text-form": return m.field_no_text_form(options);
    // Another kind of error, or a reason this version does not know: its English message.
    default: return undefined;
  }
}

/**
 * The text of a `RunicTextKey`, such as `bridge.failed` or `shortcut.page_up`, in `locale`, as `configureRunicText`
 * takes it: the catalog's message of that name; `undefined` for a key this version has no message for.
 */
export function runicViewsText(key, locale) {
  if (typeof key !== "string") return undefined;
  const name = key.replace(".", "_");
  const message = Object.hasOwn(m, name) ? m[name] : undefined;
  return typeof message === "function" ? message(locale === undefined ? undefined : { locale }) : undefined;
}

/**
 * Shows Runic's page text with the runic-views text in the window's language, in `instance` or the current Runic
 * instance (the page's by default): field errors (`FieldState.error`, `resolveText`) through `configureFieldErrorText`,
 * and the sentences of `describeBridgeError` and the key names of `formatShortcut` through `configureRunicText`.
 * Returns a function that removes both. Replaces an earlier configuration of those hooks in that instance; an
 * application with texts of its own calls `runicViewsFieldErrorText` and `runicViewsText` from its hooks instead.
 */
export function configureRunicViewsText(instance) {
  const configure = () => {
    const stopFieldErrors = configureFieldErrorText(runicViewsFieldErrorText);
    const stopText = configureRunicText(runicViewsText);
    return () => { stopText(); stopFieldErrors(); };
  };
  return instance === undefined ? configure() : instance.run(configure);
}
