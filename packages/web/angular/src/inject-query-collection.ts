import { assertInInjectionContext, DestroyRef, effect, ElementRef, inject, Injector, NgZone, signal, untracked, type Signal } from "@angular/core";
import {
  createQueryViewportController,
  type QueryCollection,
  type QueryViewport,
  type QueryViewportController,
  type QueryViewportOptions,
  type RunicQuery,
} from "@runic-artifex/views";

export interface InjectQueryCollectionOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

export interface QueryCollectionHandle<TRow> {
  /** The rows to render with their offsets, the spacer's `totalSize`, the total, query, loading and error state. */
  readonly viewport: Signal<QueryViewport<TRow>>;
  /**
   * Measures a rendered row's height until the returned function is called, for example from an `effect` over
   * `viewChildren` of the rows.
   */
  observe(element: Element | ElementRef<Element>, index: number): () => void;
  /** Scrolls to the row with `key` in the current query; resolves `false` when no row has it. */
  scrollToKey(key: string, align?: "start" | "center" | "end"): Promise<boolean>;
  scrollToIndex(index: number, align?: "start" | "center" | "end"): void;
  /** Changes the collection's sort and filter (ViewModel state every presentation shares). */
  setQuery(query: RunicQuery): Promise<void>;
  /** Loads the range again after a failure. */
  retry(): void;
}

const empty: QueryViewport<never> = { items: [], totalSize: 0, total: null, query: { sort: [], filters: [] }, loading: false, error: undefined };

/**
 * Follows a scroll container for a query collection (`client.queries.{name}`): it loads the rows the container shows
 * from .NET, with placeholders until they arrive, and lays them out with variable heights. Pass the container, for
 * example `viewChild<ElementRef<HTMLElement>>("scroller")`, the collection as a function such as
 * `() => view.client()?.queries.rows`, and the options.
 */
export function injectQueryCollection<TRow>(
  element: Signal<ElementRef<HTMLElement> | HTMLElement | null | undefined>,
  collection: () => QueryCollection<TRow> | undefined,
  options: () => QueryViewportOptions,
  { injector: provided }: InjectQueryCollectionOptions = {},
): QueryCollectionHandle<TRow> {
  if (!provided) assertInInjectionContext(injectQueryCollection);
  const injector = provided ?? inject(Injector);
  const viewport = signal<QueryViewport<TRow>>(empty);
  const zone = injector.get(NgZone, null);
  const outside = (run: () => void) => zone ? zone.runOutsideAngular(run) : run();
  let controller: QueryViewportController<TRow> | undefined;
  const container = () => {
    const target = untracked(element);
    return target instanceof ElementRef ? target.nativeElement : target ?? null;
  };
  let release: (() => void) | undefined;
  effect(() => {
    const current = collection();
    untracked(() => {
      release?.();
      release = undefined;
      if (!current) { viewport.set(empty); return; }
      const created = controller = createQueryViewportController(current, options());
      const stop = created.subscribe(() => viewport.set(created.current));
      outside(() => created.attach(container()));
      viewport.set(created.current);
      release = () => { stop(); created.dispose(); if (controller === created) controller = undefined; };
    });
  }, { injector });
  effect(() => {
    const { estimateSize, overscan } = options();
    untracked(() => controller?.update({ estimateSize, ...(overscan === undefined ? {} : { overscan }) }));
  }, { injector });
  // Scroll and resize events only update the viewport signal, which schedules change detection itself.
  effect(() => {
    element();
    untracked(() => outside(() => controller?.attach(container())));
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => { release?.(); release = undefined; });
  return {
    viewport: viewport.asReadonly(),
    observe: (target, index) => controller?.observe(target instanceof ElementRef ? target.nativeElement : target, index) ?? (() => {}),
    scrollToKey: async (key, align) => controller ? controller.scrollToKey(key, align) : false,
    scrollToIndex: (index, align) => controller?.scrollToIndex(index, align),
    setQuery: async query => {
      const current = untracked(collection);
      if (!current) throw new TypeError("injectQueryCollection has no collection yet.");
      await current.setQuery(query);
    },
    retry: () => untracked(collection)?.retry(),
  };
}
