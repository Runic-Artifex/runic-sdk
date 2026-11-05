import { assertInInjectionContext, DestroyRef, inject, Injector, PLATFORM_ID, signal, type Signal } from "@angular/core";
import { appearance, onAppearance, unknownAppearance, type Appearance } from "@runic-artifex/views";

/** Options of {@link injectAppearance}. */
export interface InjectAppearanceOptions {
  /** The injector to use outside an injection context. */
  readonly injector?: Injector;
}

/**
 * The page's appearance (`appearance` from `@runic-artifex/views`) as a signal for the lifetime of the injection
 * context: the colour scheme, contrast and reduced motion of the page's engine combined with the desktop's values that a
 * Runic host pushes (`appearance/1`), which also carry the text scale, the accent colour and the forced-colours palette.
 * Every member is `null` under server rendering.
 *
 * ```ts
 * readonly appearance = injectAppearance();
 * readonly animate = computed(() => this.appearance().reducedMotion !== true);
 * ```
 */
export function injectAppearance({ injector: provided }: InjectAppearanceOptions = {}): Signal<Appearance> {
  if (!provided) assertInInjectionContext(injectAppearance);
  const injector = provided ?? inject(Injector);
  if (injector.get(PLATFORM_ID, null) === "server") return signal(unknownAppearance).asReadonly();
  const current = signal<Appearance>(appearance());
  const stop = onAppearance(next => current.set(next));
  injector.get(DestroyRef).onDestroy(stop);
  current.set(appearance());
  return current.asReadonly();
}
