import { configureTranslatedText, syncDocumentLocale } from "@runic-artifex/views";
// The catalog's ESM, which `dotnet build` generates from Frontend/translations (see the .csproj).
import { m } from "../.runic/translations/messages.js";
import { configureSharedLocaleResolver, createLocaleSource, localeDirection, locales } from "../.runic/translations/runtime.js";
import { resolveTextReference } from "../.runic/translations/transport.js";

export { m };

/** The catalog's languages (runic.json "locales") for the language list, each named in its own language. */
export const languages = locales.map(tag => ({ tag, name: new Intl.DisplayNames([tag], { type: "language" }).of(tag) ?? tag }));

/**
 * Connects the catalog's runtime to the window's language, which the application sets (IRunicApplicationLocale):
 * the ViewModels' text references (useText) and the page's own m.*() text follow it, and <html lang dir> too.
 * Components that render m.*() call useTextLocale() to render again on a switch. Returns a function that disconnects it.
 */
export function startTranslations(initialLocale = navigator.language): () => void {
  const locale = createLocaleSource({ initialLocale });
  // Every catalog on the page follows the window's language, also those of component libraries.
  const stopResolver = configureSharedLocaleResolver(locale.getRequestedLocale);
  const stopText = configureTranslatedText({
    ...locale,
    resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
    direction: localeDirection,
  });
  const stopDocument = syncDocumentLocale();
  return () => { stopDocument(); stopText(); stopResolver(); };
}
