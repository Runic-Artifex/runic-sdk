import { FieldError } from "@runic-artifex/views";
import { runicViewsFieldErrorText, runicViewsMessages as m } from "@runic-artifex/views-translations";

const fieldError = (reason, args, locale) =>
  runicViewsFieldErrorText(new FieldError("invalid-text", "English", undefined, { reason, arguments: args }), locale);

export const texts = Object.fromEntries(["en", "de", "fr"].map(locale => [locale, {
  untitled: m.platform_untitled_file_name({ locale }),
  saveFile: m.platform_save_file_title({ locale }),
  notANumber: fieldError("not-a-number", { text: "1e" }, locale),
  notAChoice: fieldError("not-a-choice", { text: "XL", choices: ["S", "M", "L"] }, locale),
}]));
