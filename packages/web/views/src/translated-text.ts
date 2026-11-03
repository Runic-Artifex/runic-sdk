// Translated text (`translation-text/1`, specs/application/README.md, "Translated text"): text references that a
// ViewModel produces, such as status text and validation messages, are shown in the window's language by the
// application's Translations runtime. The producer pushes the window's locale; the page sets its Translations locale to
// it and resolves every reference again, without a call per string.
//
// The state is page-wide and shared by every copy of this package on the page, like the connection state.
import { reportBridgeError } from "./transport.js";
import type { TextReference } from "./wire.js";

export type { TextReference } from "./wire.js";

/**
 * The application's Translations runtime, as `configureTranslatedText` takes it. With the generated ESM of Runic
 * Translations:
 *
 * ```ts
 * import { m } from "virtual:runic-translations/app/messages";
 * import { configureLocaleResolver, createLocaleSource } from "virtual:runic-translations/app/runtime";
 * import { resolveTextReference } from "virtual:runic-translations/app/transport";
 *
 * const locale = createLocaleSource({ initialLocale: navigator.language });
 * configureLocaleResolver(locale.getLocale);
 * configureTranslatedText({
 *   ...locale,
 *   resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
 * });
 * ```
 */
export interface TextTranslations {
  /**
   * Formats a reference in a locale, or returns `undefined` when it cannot, such as `resolveTextReference(reference, m,
   * { locale }).text`. A reference it cannot format shows its `fallbackText`.
   */
  resolve(reference: TextReference, locale: string): string | undefined;
  /** The locale the page shows, such as the `getLocale` of a Translations locale source. */
  getLocale(): string;
  /**
   * Sets the page's locale: the window's locale is passed here when the application sets it, so the page's own
   * messages follow too. Without it, references resolve in the window's locale and the page's messages keep theirs.
   */
  setLocale?(locale: string): void;
  /** Observes the page's locale, such as a locale source's `subscribe`, so a switch on the page updates the text. */
  subscribe?(listener: (locale: string) => void): () => void;
}

interface TranslatedTextChannel {
  // The last locale the producer pushed; null while the application sets none.
  windowLocale: string | null;
  translations?: TextTranslations | undefined;
  stopTranslations?: (() => void) | undefined;
  readonly listeners: Set<() => void>;
  readonly localeListeners: Set<(locale: string | null) => void>;
  // Changes with every change of the text: listeners compare it to skip work.
  version: number;
}

const channelKey = Symbol.for("runic.views.translated-text");

function channel(): TranslatedTextChannel {
  const host = globalThis as unknown as Record<symbol, TranslatedTextChannel | undefined>;
  return host[channelKey] ??= { windowLocale: null, listeners: new Set(), localeListeners: new Set(), version: 0 };
}

function changed(): void {
  const state = channel();
  state.version++;
  for (const listener of [...state.listeners]) {
    try { listener(); } catch (error) { reportBridgeError(error); }
  }
}

/**
 * Connects the page's Translations runtime: `resolveText`, `useText` and the framework helpers format references with
 * it, and the window's locale, when the application sets one, is passed to its `setLocale`. Returns a function that
 * disconnects it. A later call replaces an earlier one.
 */
export function configureTranslatedText(translations: TextTranslations): () => void {
  if (typeof translations?.resolve !== "function" || typeof translations.getLocale !== "function")
    throw new TypeError("configureTranslatedText needs resolve(reference, locale) and getLocale().");
  const state = channel();
  state.stopTranslations?.();
  state.translations = translations;
  let stopped = false;
  const stopLocale = translations.subscribe?.(() => { if (!stopped) changed(); });
  const stop = () => {
    if (stopped) return;
    stopped = true;
    stopLocale?.();
    if (state.translations === translations) {
      state.translations = undefined;
      state.stopTranslations = undefined;
      changed();
    }
  };
  state.stopTranslations = stop;
  // A locale the window pushed before the runtime was connected.
  if (state.windowLocale !== null) followWindowLocale(translations, state.windowLocale);
  changed();
  return stop;
}

function followWindowLocale(translations: TextTranslations, locale: string): void {
  try { translations.setLocale?.(locale); } catch (error) { reportBridgeError(error); }
}

/**
 * The window's locale, a BCP 47 tag such as `"de"`, which the application set for this window (`IRunicWindowLocale` in
 * .NET), or `null` while it sets none and the page keeps its own (a producer of 0.16 or later, `translation-text/1`).
 */
export function windowLocale(): string | null {
  return channel().windowLocale;
}

/** Calls `listener` with the window's locale each time the application changes it, until the returned function is called. */
export function onWindowLocale(listener: (locale: string | null) => void): () => void {
  const listeners = channel().localeListeners;
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/**
 * The locale in which references are shown: the Translations runtime's locale, or the window's locale when the runtime
 * cannot follow it. `undefined` without a runtime and a window locale.
 */
export function textLocale(): string | undefined {
  const { translations, windowLocale: locale } = channel();
  if (!translations) return locale ?? undefined;
  if (locale !== null && !translations.setLocale) return locale;
  return translations.getLocale();
}

/**
 * The text to show for a value of a `TextReference` member: the reference formatted in the window's language
 * ({@link textLocale}), its `fallbackText` when it cannot be formatted or no Translations runtime is configured, then
 * `fallback`, then its key. A string is shown as it is, so a member may carry either; `null` and `undefined` show
 * `fallback` or `""`. For a validation error: `resolveText(error.text, error.message)`.
 *
 * The text is plain: show it as text, never as HTML.
 */
export function resolveText(value: TextReference | string | null | undefined, fallback?: string): string {
  if (value === null || value === undefined) return fallback ?? "";
  if (typeof value === "string") return value;
  const { translations } = channel();
  const locale = textLocale();
  if (translations && locale !== undefined) {
    try {
      const text = translations.resolve(value, locale);
      if (typeof text === "string") return text;
    } catch (error) { reportBridgeError(error); }
  }
  return value.fallbackText ?? fallback ?? value.key;
}

/**
 * Calls `listener` whenever text resolves differently: the window's locale or the page's Translations locale changed,
 * or a runtime was configured. Returns a function that stops it. Framework helpers render again on it.
 */
export function subscribeText(listener: () => void): () => void {
  const listeners = channel().listeners;
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** A number that changes whenever text resolves differently, for framework snapshots. */
export function textVersion(): number {
  return channel().version;
}

/** @internal The page runtime reports the window's locale that the producer pushed. */
export function reportWindowLocale(locale: string | null): void {
  const state = channel();
  if (state.windowLocale === locale) return;
  state.windowLocale = locale;
  if (locale !== null && state.translations) followWindowLocale(state.translations, locale);
  for (const listener of [...state.localeListeners]) {
    try { listener(locale); } catch (error) { reportBridgeError(error); }
  }
  changed();
}
