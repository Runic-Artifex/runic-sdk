import { appearance, onAppearance, unknownAppearance, type Appearance } from "@runic-artifex/views";
import { getRunicInstance } from "./instance.js";

export interface AppearanceHandle {
  /** The page's appearance. A reactive property; every member is `null` under server rendering. */
  readonly current: Appearance;
}

/**
 * Follows the page's appearance (`appearance` from `@runic-artifex/views`) while its owner lives: the colour scheme,
 * contrast and reduced motion of the page's engine combined with the desktop's values that a Runic host pushes
 * (`appearance/1`), which also carry the text scale, the accent colour and the forced-colours palette.
 *
 * ```svelte
 * <script lang="ts">
 *   const look = useAppearance();
 * </script>
 * <main style:font-size="{look.current.textScale ?? 1}rem">…</main>
 * ```
 */
export function useAppearance(): AppearanceHandle {
  const instance = getRunicInstance();
  let current = $state.raw<Appearance>(typeof window === "undefined" ? unknownAppearance : instance.run(appearance));
  $effect.pre(() => instance.run(() => {
    const stop = onAppearance(next => { current = next; });
    current = appearance();
    return stop;
  }));
  return { get current() { return current; } };
}
