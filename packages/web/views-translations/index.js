// Runic's own page text from the runic-views catalog (English and German): field errors, and the references of
// Runic.Application.Views.Translations' validation texts. The catalog is imported by a relative path, so an application
// whose Vite build uses runicTranslations({ dependencies: "auto" }) gets it regenerated with its overrides and locales.
import { configureFieldErrorText } from "@runic-artifex/views";
import { m } from "./dist/translations/runic-views.esm-v5/messages.js";
import * as transport from "./dist/translations/runic-views.esm-v5/transport.js";
import { getRequestedLocale } from "./dist/translations/runic-views.esm-v5/runtime.js";

export { m as runicViewsMessages, transport as runicViewsTransport };

/** The catalog as an entry of a generated transport's `createReferenceResolver`. */
export const runicViewsCatalog = Object.freeze({ transport, messages: m });

/** Formats a `runic-views` reference in `locale`; `undefined` for a reference of another catalog or one it cannot format. */
export function resolveRunicViewsText(reference, locale) {
  if (typeof reference !== "object" || reference === null || reference.catalog !== transport.catalog) return undefined;
  const resolution = transport.resolveTextReference(reference, m, locale === undefined ? undefined : { locale });
  return resolution.outcome === "translated" ? resolution.text : undefined;
}

function list(items, locale, type) {
  const values = [...(items ?? [])];
  try {
    return new Intl.ListFormat(locale ?? getRequestedLocale(), { type, style: type === "unit" ? "short" : "long" }).format(values);
  } catch {
    return values.join(", ");
  }
}

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
    case "not-a-choice": return m.field_not_a_choice({ text, choices: list(args.choices, locale, "unit") }, options);
    case "not-a-date":
      return args.patterns?.length ? m.field_not_a_date_in({ text, patterns: list(args.patterns, locale, "disjunction") }, options)
        : m.field_not_a_date({ text }, options);
    case "not-a-time":
      return args.patterns?.length ? m.field_not_a_time_in({ text, patterns: list(args.patterns, locale, "disjunction") }, options)
        : m.field_not_a_time({ text }, options);
    case "not-a-date-time": return m.field_not_a_date_time({ text, patterns: list(args.patterns, locale, "disjunction") }, options);
    case "offset-out-of-range": return m.field_offset_out_of_range({ text }, options);
    case "no-text-form": return m.field_no_text_form(options);
    // Another kind of error, or a reason this version does not know: its English message.
    default: return undefined;
  }
}

/**
 * Shows field errors (`FieldState.error`, `describeBridgeError`) with the runic-views text in the window's language.
 * Returns a function that removes it. Replaces an earlier `configureFieldErrorText`; an application with texts of its own
 * calls `runicViewsFieldErrorText` from its hook instead.
 */
export function configureRunicViewsText() {
  return configureFieldErrorText(runicViewsFieldErrorText);
}
