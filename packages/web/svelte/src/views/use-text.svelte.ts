import { onWindowLocale, resolveText, subscribeText, windowLocale, type TextReference } from "@runic-artifex/views";

export interface TextHandle {
  /** The text in the window's language. A reactive property. */
  readonly current: string;
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
 * fallback text. Call during component initialization (or in `$effect.root`).
 *
 * ```svelte
 * const status = useText(() => view.state?.status);
 * <p>{status.current}</p>
 * ```
 */
export function useText(source: () => TextReference | string | null | undefined, fallback?: () => string | undefined): TextHandle {
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
