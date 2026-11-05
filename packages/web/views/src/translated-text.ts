// Translated text (`translation-text/1`, specs/application/README.md, "Translated text"): text references that a
// ViewModel produces, such as status text and validation messages, are shown in the window's language by the
// application's Translations runtime. The producer pushes the window's locale; the page sets its Translations locale to
// it and resolves every reference again, without a call per string.
//
// The state is page-wide and shared by every copy of this package on the page, like the connection state.
import { isFieldError, type FieldError, type FieldErrorArguments } from "./field-error.js";
import { reportBridgeError } from "./transport.js";
import type { TextReference } from "./wire.js";

export type { TextReference } from "./wire.js";

/**
 * The application's Translations runtime, as `configureTranslatedText` takes it. With the generated ESM of Runic
 * Translations:
 *
 * ```ts
 * import { m } from "virtual:runic-translations/app/messages";
 * import { configureLocaleResolver, createLocaleSource, localeDirection } from "virtual:runic-translations/app/runtime";
 * import { resolveTextReference } from "virtual:runic-translations/app/transport";
 *
 * const locale = createLocaleSource({ initialLocale: navigator.language });
 * configureLocaleResolver(locale.getLocale);
 * configureTranslatedText({
 *   ...locale,
 *   resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
 *   direction: localeDirection,
 * });
 * ```
 *
 * A template that calls `m.*()` renders again on a language switch when the locale resolver reads the framework's
 * reactive text locale (`useTextLocale` in React, Vue and Svelte, `textLocaleSignal` in Angular).
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
  /**
   * The writing direction of a locale, such as the generated runtime's `localeDirection`. {@link textDirection} and
   * {@link syncDocumentLocale} use it; without it, the direction comes from the locale's script as `Intl.Locale` gives it.
   */
  direction?(locale: string): "ltr" | "rtl";
}

interface TranslatedTextChannel {
  // The last locale the producer pushed; null while the application sets none.
  windowLocale: string | null;
  translations?: TextTranslations | undefined;
  stopTranslations?: (() => void) | undefined;
  fieldErrorText?: FieldErrorText | undefined;
  // The locales each field error's throwing hook has been reported for; replaced with each configured hook.
  fieldErrorFailures?: WeakMap<object, Set<string | undefined>> | undefined;
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
 * The application's text for a field error, as `configureFieldErrorText` takes it: the text to show for `error` in
 * `locale` (the locale references are shown in, {@link textLocale}), or `undefined` to show its English `message`.
 * Runic's parsers give an `invalid-text` error a `reason` and `arguments`; the other kinds carry the producer's
 * message. With the generated ESM of Runic Translations and messages named after the reasons:
 *
 * ```ts
 * configureFieldErrorText((error, locale) => {
 *   const { text = "", scale = 0 } = error.arguments;
 *   const options = locale === undefined ? {} : { locale };
 *   if (error.reason === "not-a-number") return m.field_not_a_number({ text }, options);
 *   if (error.reason === "too-many-fraction-digits") return m.field_too_many_fraction_digits({ text, scale }, options);
 *   return undefined;
 * });
 * ```
 */
export type FieldErrorText = (error: FieldError, locale: string | undefined) => string | undefined;

/**
 * Sets the application's text for field errors (`FieldState.error`): {@link resolveText}, the framework packages'
 * `useText` and `runicText`, and `describeBridgeError` show a `FieldError` with it, in the window's language. Returns
 * a function that removes it. A later call replaces an earlier one. Page-wide, like `configureTranslatedText`.
 */
export function configureFieldErrorText(text: FieldErrorText): () => void {
  if (typeof text !== "function") throw new TypeError("configureFieldErrorText needs a function (error, locale) => text.");
  const state = channel();
  state.fieldErrorText = text;
  state.fieldErrorFailures = new WeakMap();
  changed();
  return () => {
    if (state.fieldErrorText !== text) return;
    state.fieldErrorText = undefined;
    changed();
  };
}

const emptyArguments: FieldErrorArguments = Object.freeze({});

// The error the hook reads: one of an older copy of this package has no `arguments`, which reads as none. The view
// inherits everything else, so the brand and `instanceof` still hold, and the original error is not changed.
function hookView(error: FieldError): FieldError {
  return error.arguments === undefined ? Object.create(error, { arguments: { value: emptyArguments } }) : error;
}

/**
 * The text to show for a field error: the text `configureFieldErrorText` gives it in {@link textLocale}, or its
 * English `message`. A `FieldError` of another copy of this package is read the same way, and a `reason` that is
 * `undefined` for it falls back to the message. A throwing hook is reported once per error and locale.
 */
export function fieldErrorText(error: FieldError): string {
  const state = channel();
  const text = state.fieldErrorText;
  if (text !== undefined) {
    const locale = textLocale();
    try {
      const shown = text(hookView(error), locale);
      if (typeof shown === "string") return shown;
    } catch (failure) {
      const failures = state.fieldErrorFailures ??= new WeakMap();
      let locales = failures.get(error);
      if (locales === undefined) failures.set(error, locales = new Set());
      if (!locales.has(locale)) {
        locales.add(locale);
        reportBridgeError(failure);
      }
    }
  }
  return error.message;
}

/**
 * The text to show for a value of a `TextReference` member: the reference formatted in the window's language
 * ({@link textLocale}), its `fallbackText` when it cannot be formatted or no Translations runtime is configured, then
 * `fallback`, then its key. A string is shown as it is, so a member may carry either; `null` and `undefined` show
 * `fallback` or `""`. For a validation error: `resolveText(error.text, error.message)`. A field's `FieldError` shows
 * the application's text for it ({@link fieldErrorText}).
 *
 * The text is plain: show it as text, never as HTML.
 */
export function resolveText(value: TextReference | FieldError | string | null | undefined, fallback?: string): string {
  if (value === null || value === undefined) return fallback ?? "";
  if (typeof value === "string") return value;
  if (isFieldError(value)) return fieldErrorText(value);
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
 * or a runtime or the field error text was configured. Returns a function that stops it. Framework helpers render again on it.
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

/**
 * @internal The page runtime reports the window's locale that the producer pushed. Every push is applied to the
 * Translations runtime, also one that repeats the last value: the page may have changed its runtime's locale itself
 * since, and a push after a reconnect restores the window's. Listeners hear only changes.
 */
export function reportWindowLocale(locale: string | null): void {
  const state = channel();
  if (locale !== null && state.translations) followWindowLocale(state.translations, locale);
  if (state.windowLocale === locale) return;
  state.windowLocale = locale;
  for (const listener of [...state.localeListeners]) {
    try { listener(locale); } catch (error) { reportBridgeError(error); }
  }
  changed();
}

// Scripts written right to left (CLDR), for a runtime without `direction` and a browser without `Intl.Locale` text info.
const rightToLeftScripts = new Set(["Adlm", "Arab", "Hebr", "Mand", "Mend", "Nkoo", "Rohg", "Samr", "Syrc", "Thaa", "Yezi"]);

function scriptDirection(locale: string): "ltr" | "rtl" {
  try {
    const tag = new Intl.Locale(locale) as Intl.Locale & {
      getTextInfo?: () => { direction?: string }; textInfo?: { direction?: string };
    };
    const info = typeof tag.getTextInfo === "function" ? tag.getTextInfo() : tag.textInfo;
    if (info?.direction === "rtl" || info?.direction === "ltr") return info.direction;
    const script = tag.maximize().script;
    return script !== undefined && rightToLeftScripts.has(script) ? "rtl" : "ltr";
  } catch {
    return "ltr";
  }
}

/**
 * The writing direction of the text locale ({@link textLocale}): the Translations runtime's `direction`, or the
 * direction of the locale's script. `undefined` while there is no text locale.
 */
export function textDirection(): "ltr" | "rtl" | undefined {
  const locale = textLocale();
  if (locale === undefined || locale === "") return undefined;
  const direction = channel().translations?.direction;
  if (typeof direction === "function") {
    try {
      const value = direction(locale);
      if (value === "ltr" || value === "rtl") return value;
    } catch (error) { reportBridgeError(error); }
  }
  return scriptDirection(locale);
}

/** Where {@link syncDocumentLocale} sets the language. */
export interface DocumentLocaleOptions {
  /** The element whose `lang` and `dir` follow the text locale. Defaults to `document.documentElement`. */
  readonly element?: Element;
  /** Whether `dir` follows too ({@link textDirection}). Defaults to `true`. */
  readonly direction?: boolean;
}

/**
 * Sets `lang` and `dir` of the page's root element to the language that text references are shown in
 * ({@link textLocale}, {@link textDirection}), now and after every language switch, until the returned function is
 * called. While the page has no text locale, the element keeps the attributes it has, such as `<html lang="en">`.
 * Call it once in the browser, for example in `main.ts` after `configureTranslatedText`; without a document it does
 * nothing.
 */
export function syncDocumentLocale(options: DocumentLocaleOptions = {}): () => void {
  const element = options.element ?? (typeof document === "undefined" ? undefined : document.documentElement);
  if (!element) return () => {};
  const withDirection = options.direction !== false;
  const apply = () => {
    const locale = textLocale();
    if (locale === undefined || locale === "") return;
    if (element.getAttribute("lang") !== locale) element.setAttribute("lang", locale);
    const direction = withDirection ? textDirection() : undefined;
    if (direction !== undefined && element.getAttribute("dir") !== direction) element.setAttribute("dir", direction);
  };
  apply();
  return subscribeText(apply);
}
