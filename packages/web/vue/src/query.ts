import { shallowRef, toValue, watch, type ComponentPublicInstance, type MaybeRefOrGetter, type Ref } from "vue";
import {
  createQueryViewportController,
  type QueryCollection,
  type QueryViewport,
  type QueryViewportController,
  type QueryViewportOptions,
  type RunicQuery,
} from "@runic-artifex/views";
import { releaseWithScope } from "./scope.js";

export interface QueryCollectionHandle<TRow> {
  /** The rows to render with their offsets, the spacer's `totalSize`, the total, query, loading and error state. */
  readonly viewport: Readonly<Ref<QueryViewport<TRow>>>;
  /** A function ref for the scroll container, bound to a native element: `<div :ref="attach">`. */
  attach(element: Element | ComponentPublicInstance | null): void;
  /** A function ref for each rendered row, which measures its height: `<div :ref="el => measure(el, item.index)">`. */
  measure(element: Element | ComponentPublicInstance | null, index: number): void;
  /** Scrolls to the row with `key` in the current query; resolves `false` when no row has it. */
  scrollToKey(key: string, align?: "start" | "center" | "end"): Promise<boolean>;
  scrollToIndex(index: number, align?: "start" | "center" | "end"): void;
  /** Changes the collection's sort and filter (ViewModel state every presentation shares). */
  setQuery(query: RunicQuery): Promise<void>;
  /** Loads the range again after a failure. */
  retry(): void;
}

/**
 * Follows a scroll container for a query collection (`client.queries.{name}`): it loads the rows the container shows
 * from .NET, with placeholders until they arrive, and lays them out with variable heights. Pass the collection or a
 * getter of it (`() => client.value?.queries.rows`). Call it in `setup()` or `effectScope().run()`.
 */
export function useQueryCollection<TRow>(collection: MaybeRefOrGetter<QueryCollection<TRow> | undefined>,
  options: MaybeRefOrGetter<QueryViewportOptions>): QueryCollectionHandle<TRow> {
  const empty: QueryViewport<TRow> = { items: [], totalSize: 0, total: null, query: { sort: [], filters: [] }, loading: false, error: undefined };
  const viewport = shallowRef<QueryViewport<TRow>>(empty);
  let controller: QueryViewportController<TRow> | undefined;
  let stop: (() => void) | undefined;
  let container: HTMLElement | null = null;
  const rows = new Map<number, () => void>();
  const release = () => {
    stop?.();
    stop = undefined;
    for (const unobserve of rows.values()) unobserve();
    rows.clear();
    controller?.dispose();
    controller = undefined;
  };
  watch(() => toValue(collection), next => {
    release();
    if (!next) { viewport.value = empty; return; }
    const created = controller = createQueryViewportController(next, toValue(options));
    stop = created.subscribe(() => { viewport.value = created.current; });
    created.attach(container);
    viewport.value = created.current;
  }, { immediate: true });
  watch(() => {
    const { estimateSize, overscan } = toValue(options);
    return [estimateSize, overscan] as const;
  }, ([estimateSize, overscan]) => {
    controller?.update({ estimateSize, ...(overscan === undefined ? {} : { overscan }) });
  });
  releaseWithScope("useQueryCollection", release);
  const element = (value: Element | ComponentPublicInstance | null) =>
    typeof HTMLElement !== "undefined" && value instanceof HTMLElement ? value : null;
  return {
    viewport,
    attach(value) {
      container = element(value);
      controller?.attach(container);
    },
    measure(value, index) {
      rows.get(index)?.();
      rows.delete(index);
      const row = element(value);
      if (row && controller) rows.set(index, controller.observe(row, index));
    },
    scrollToKey: async (key, align) => controller ? controller.scrollToKey(key, align) : false,
    scrollToIndex: (index, align) => controller?.scrollToIndex(index, align),
    setQuery: async query => {
      const current = toValue(collection);
      if (!current) throw new TypeError("useQueryCollection has no collection yet.");
      await current.setQuery(query);
    },
    retry: () => toValue(collection)?.retry(),
  };
}
