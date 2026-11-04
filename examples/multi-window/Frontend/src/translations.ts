import type { TextReference, TextTranslations } from "@runic-artifex/views";

// The page's side of ProjectText.cs: each message in English and German. An application generates this from its Runic
// Translations catalog and passes `resolveTextReference(reference, m, { locale }).text` of the generated transport.js
// instead (packages/dotnet/Runic.Application.Views/README.md, "Translated text").
type Inputs = Readonly<Record<string, string>>;
const messages: Readonly<Record<string, Readonly<Record<string, (inputs: Inputs) => string>>>> = {
  en: {
    ready: () => "Ready.",
    renamed: inputs => `Renamed to ${inputs["title"]}.`,
    title_too_long: inputs => `The title must be at most ${inputs["max"]} characters.`,
  },
  de: {
    ready: () => "Bereit.",
    renamed: inputs => `Umbenannt in ${inputs["title"]}.`,
    title_too_long: inputs => `Der Titel darf höchstens ${inputs["max"]} Zeichen lang sein.`,
  },
};

// Each argument's canonical value, which is how the Translations transport writes it.
function inputsOf(reference: TextReference): Inputs {
  const inputs: Record<string, string> = {};
  for (const [name, argument] of Object.entries(reference.arguments ?? {}))
    inputs[name] = String((argument as { readonly value: unknown }).value);
  return inputs;
}

/** A locale source and resolver for configureTranslatedText: it follows the Window's locale. */
export function createTranslations(initialLocale: string): TextTranslations {
  let locale = initialLocale in messages ? initialLocale : "en";
  const listeners = new Set<(locale: string) => void>();
  return {
    resolve: (reference, language) => reference.catalog === "multi-window"
      ? messages[language]?.[reference.key]?.(inputsOf(reference))
      : undefined,
    getLocale: () => locale,
    setLocale(next) {
      locale = next in messages ? next : "en";
      for (const listener of [...listeners]) listener(locale);
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
  };
}
