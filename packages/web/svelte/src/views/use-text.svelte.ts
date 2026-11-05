import { createSubscriber } from "svelte/reactivity";
import { onWindowLocale, resolveText, subscribeText, textLocale, windowLocale, type FieldError, type TextReference } from "@runic-artifex/views";

export interface TextHandle {
  /** The text in the window's language. A reactive property. */
  readonly current: string;
}

export interface TextLocaleHandle {
  /** The locale that text is shown in, or `undefined` before there is one. A reactive property. */
  readonly current: string | undefined;
}

export interface WindowLocaleHandle {
  /** The window's locale that the application set, or `null` while it sets none. A reactive property. */
  readonly current: string | null;
}

/**
 * Follows a text reference that a ViewModel produced, such as a status line or a validation message's `text`, in the
 * window's language (`resolveText`), while its owner lives. `source` and `fallback` are read reactively, so pass
 * functions: `useText(() => view.state?.status)`. A string shows as it is; `null` and `undefined` show `fallback`.
 * Configure the page's Translations runtime once with `configureTranslatedText`; without one, a reference shows its
 * fallback text. A field's `FieldError` shows the application's text for it (`configureFieldErrorText`), or its English
 * message: `useText(() => name.error)`. Call during component initialization (or in `$effect.root`).
 *
 * ```svelte
 * const status = useText(() => view.state?.status);
 * <p>{status.current}</p>
 * ```
 */
export function useText(source: () => TextReference | FieldError | string | null | undefined, fallback?: () => string | undefined): TextHandle {
  let version = $state(0);
  $effect.pre(() => subscribeText(() => { version++; }));
  const current = $derived.by(() => {
    void version;
    return resolveText(source(), fallback?.());
  });
  return { get current() { return current; } };
}

/** Follows the window's locale (`windowLocale`) while its owner lives, for example to set `lang`. */
export function useWindowLocale(): WindowLocaleHandle {
  let current = $state<string | null>(windowLocale());
  $effect.pre(() => onWindowLocale(next => { current = next; }));
  return { get current() { return current; } };
}

const subscribeTextLocale = createSubscriber(update => subscribeText(update));
const textLocaleHandle: TextLocaleHandle = {
  get current() {
    subscribeTextLocale();
    return textLocale();
  },
};

/**
 * Follows the locale that text is shown in (`textLocale`: the page's Translations locale, which follows the window's
 * locale). Reading `current` in a template, `$derived` or `$effect` runs it again when the locale changes. It is one
 * handle for the page and needs no component, so the page's locale resolver can read it: a template that calls
 * `m.*()` then renders again on a language switch. `undefined` while the page has no Translations runtime and the
 * application sets no window locale.
 *
 * ```ts
 * const textLocale = useTextLocale();
 * configureLocaleResolver(() => textLocale.current ?? locale.getLocale());
 * ```
 */
export function useTextLocale(): TextLocaleHandle {
  return textLocaleHandle;
}
