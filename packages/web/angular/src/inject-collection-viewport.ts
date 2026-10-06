import { assertInInjectionContext, DestroyRef, effect, ElementRef, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import { createCollectionViewportController, type CollectionViewport, type CollectionViewportOptions } from "@runic-artifex/views";

export interface InjectCollectionViewportOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET as a signal. Pass the container, for example
 * `viewChild<ElementRef<HTMLElement>>("scroller")`, and the list options as a
 * signal or function, then send `start` and `size` to the ViewModel from an effect.
 */
export function injectCollectionViewport(
  element: Signal<ElementRef<HTMLElement> | HTMLElement | null | undefined>,
  options: () => CollectionViewportOptions,
  { injector: provided }: InjectCollectionViewportOptions = {},
): Signal<CollectionViewport> {
  if (!provided) assertInInjectionContext(injectCollectionViewport);
  const injector = provided ?? inject(Injector);
  const controller = createCollectionViewportController(untracked(options));
  const viewport = signal(controller.current);
  controller.subscribe(() => viewport.set(controller.current));
  effect(() => {
    const { totalCount, rowHeight, overscan } = options();
    untracked(() => controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) }));
  }, { injector });
  effect(() => {
    const target = element();
    untracked(() => controller.attach(target instanceof ElementRef ? target.nativeElement : target));
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return viewport.asReadonly();
}
