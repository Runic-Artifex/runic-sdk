// client-languages/1 (specs/application/README.md, "Client languages"): the page reports its browser's preferred
// languages in every `hello`, so the application can choose the window's locale. The window keeps one locale for all
// of its pages; the languages are a preference, not a request.

import { currentSlots, type Slots } from "./scope.js";

export const clientLanguagesFeature = "client-languages/1";

const maximumLanguages = 16;

function isLanguageTag(value: string): boolean {
  return value.length >= 1 && value.length <= 64 && /^[A-Za-z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$/.test(value);
}

/** Options of {@link configureClientLanguages}. */
export interface ClientLanguagesOptions {
  /**
   * Whether each `hello` reports the browser's `navigator.languages`. Default `true`. With `false`, the page sends
   * `languages: []`: the application cannot choose the window's language from the browser, and the list, which is
   * personal and can help fingerprint the browser, does not leave the page.
   */
  readonly send?: boolean;
}

// Whether `hello` reports the languages, per instance and shared by every copy of this package; absent means `true`.
const sendKey = Symbol.for("runic.views.client-languages");

/**
 * Configures what the current instance (the page, outside `RunicInstance.run`) reports in `hello` for
 * `client-languages/1`. Call it before the page connects, in
 * `main.ts`; a later `hello`, such as after a reconnect, uses the new value. Returns a function that restores the
 * previous configuration.
 *
 * ```ts
 * configureClientLanguages({ send: false });
 * ```
 */
export function configureClientLanguages(options: ClientLanguagesOptions): () => void {
  const slots = currentSlots();
  const previous = slots[sendKey];
  slots[sendKey] = options.send ?? true;
  return () => { slots[sendKey] = previous; };
}

/**
 * @internal The `languages` of a `hello`: `navigator.languages`, or `[navigator.language]` without a list, keeping the
 * language tags in order, each once (compared without case), at most 16. `[]` without a navigator, or when
 * {@link configureClientLanguages} turned it off.
 */
export function clientLanguages(slots: Slots): string[] {
  if (slots[sendKey] === false) return [];
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

/**
 * @internal A language tag in the case BCP 47 recommends, as a producer keeps it: the language lower case, a script
 * title case, a region upper case, and the rest, including the subtags after a singleton, lower case.
 */
export function canonicalLanguageCase(tag: string): string {
  let afterSingleton = false;
  return tag.split("-").map((subtag, index) => {
    if (index === 0 || afterSingleton) return subtag.toLowerCase();
    if (subtag.length === 1) { afterSingleton = true; return subtag.toLowerCase(); }
    if (subtag.length === 4 && /^[a-z]/i.test(subtag)) return subtag[0]!.toUpperCase() + subtag.slice(1).toLowerCase();
    if (subtag.length === 2 || (subtag.length === 3 && /^\d/.test(subtag))) return subtag.toUpperCase();
    return subtag.toLowerCase();
  }).join("-");
}

/** @internal Whether a string is a language tag of client-languages/1. */
export function isClientLanguageTag(value: string): boolean {
  return isLanguageTag(value);
}
