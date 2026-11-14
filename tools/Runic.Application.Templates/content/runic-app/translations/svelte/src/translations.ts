// The types of the virtual:runic-translations modules, which the Translations Vite plugin writes when Vite runs
// (vite build, the development server or vitest): `dotnet build` or `dotnet runic dev` writes them before a type check.
/// <reference path="../.runic/translations/virtual.d.ts" />
import { configureTranslatedText, syncDocumentLocale } from "@runic-artifex/views";
import { useTextLocale } from "@runic-artifex/svelte";
// The catalog of Frontend/translations, which runicTranslations() in vite.config.ts compiles.
import { m } from "virtual:runic-translations/app";
import { configureSharedLocaleResolver, createLocaleSource, localeDirection, locales } from "virtual:runic-translations/app/runtime";
import { resolveTextReference } from "virtual:runic-translations/app/transport";

export { m };

/** The catalog's languages (runic.json "locales") for the language list, each named in its own language. */
export const languages = locales.map(tag => ({ tag, name: new Intl.DisplayNames([tag], { type: "language" }).of(tag) ?? tag }));

/**
 * Connects the catalog's runtime to the window's language, which the application sets (IRunicApplicationLocale):
 * the ViewModels' text references (useText) and the page's own m.*() text follow it, and <html lang dir> too.
 * Templates that call m.*() render again on a switch: the resolver reads the reactive text locale. Returns a function that disconnects it.
 */
export function startTranslations(initialLocale = navigator.language): () => void {
  const locale = createLocaleSource({ initialLocale });
  const textLocale = useTextLocale();
  // Every catalog on the page follows the window's language, also those of component libraries.
  const stopResolver = configureSharedLocaleResolver(() => textLocale.current ?? locale.getRequestedLocale());
  const stopText = configureTranslatedText({
    ...locale,
    resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
    direction: localeDirection,
  });
  const stopDocument = syncDocumentLocale();
  return () => { stopDocument(); stopText(); stopResolver(); };
}
