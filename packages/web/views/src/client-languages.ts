// client-languages/1 (specs/application/README.md, "Client languages"): the page reports its browser's preferred
// languages in every `hello`, so the application can choose the window's locale. The window keeps one locale for all
// of its pages; the languages are a preference, not a request.

export const clientLanguagesFeature = "client-languages/1";

const maximumLanguages = 16;

function isLanguageTag(value: string): boolean {
  return value.length >= 1 && value.length <= 64 && /^[A-Za-z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$/.test(value);
}

/**
 * @internal The `languages` of a `hello`: `navigator.languages`, or `[navigator.language]` without a list, keeping the
 * language tags in order, each once (compared without case), at most 16. `[]` without a navigator.
 */
export function clientLanguages(): string[] {
  const navigatorLike = (globalThis as { navigator?: { languages?: unknown; language?: unknown } }).navigator;
  const listed = Array.isArray(navigatorLike?.languages) && navigatorLike.languages.length > 0
    ? navigatorLike.languages as unknown[]
    : [navigatorLike?.language];
  const languages: string[] = [];
  for (const language of listed) {
    if (typeof language !== "string" || !isLanguageTag(language)) continue;
    if (languages.some(known => known.toLowerCase() === language.toLowerCase())) continue;
    languages.push(language);
    if (languages.length === maximumLanguages) break;
  }
  return languages;
}
