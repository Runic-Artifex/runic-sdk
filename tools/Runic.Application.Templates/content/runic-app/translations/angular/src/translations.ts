import { textLocaleSignal } from "@runic-artifex/angular";
import { configureTranslatedText, syncDocumentLocale } from "@runic-artifex/views";
// The catalog's ESM, which `dotnet build` generates from Frontend/translations (see the .csproj).
import { m } from "../.runic/translations/messages.js";
import { configureSharedLocaleResolver, createLocaleSource, localeDirection } from "../.runic/translations/runtime.js";
import { resolveTextReference } from "../.runic/translations/transport.js";

export { m };

/**
 * Connects the catalog's runtime to the window's language, which the application sets (IRunicApplicationLocale):
 * the ViewModels' text references (the runicText pipe) and the page's own m.*() text follow it, and <html lang dir>
 * too. Templates that call m.*() render again on a switch: the resolver reads the text locale signal. Returns a
 * function that disconnects it.
 */
export function startTranslations(initialLocale = navigator.language): () => void {
  const locale = createLocaleSource({ initialLocale });
  const textLocale = textLocaleSignal();
  // Every catalog on the page follows the window's language, also those of component libraries.
  const stopResolver = configureSharedLocaleResolver(() => textLocale() ?? locale.getRequestedLocale());
  const stopText = configureTranslatedText({
    ...locale,
    resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
    direction: localeDirection,
  });
  const stopDocument = syncDocumentLocale();
  return () => { stopDocument(); stopText(); stopResolver(); };
}
