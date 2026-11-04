import { untrack } from "svelte";
import type { Attachment } from "svelte/attachments";
import {
  createQueryViewportController,
  type QueryCollection,
  type QueryViewport,
  type QueryViewportController,
  type QueryViewportOptions,
  type RunicQuery,
} from "@runic-artifex/views";

export interface QueryCollectionHandle<TRow> {
  /** The rows to render with their offsets, the spacer's `totalSize`, the total, query, loading and error state. */
  readonly viewport: QueryViewport<TRow>;
  /** Attach to the scroll container: `<div {@attach list.attach}>`. */
  readonly attach: Attachment<HTMLElement>;
  /** Attach to each rendered row, so its height is measured: `<div {@attach list.measure(item.index)}>`. */
  measure(index: number): Attachment<Element>;
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
 * from .NET, with placeholders until they arrive, and lays them out with variable heights. Call it during component
 * initialization with getters, such as `useQueryCollection(() => view.client?.queries.rows, () => ({ estimateSize: 32 }))`.
 */
export function useQueryCollection<TRow>(collection: () => QueryCollection<TRow> | undefined,
  options: () => QueryViewportOptions): QueryCollectionHandle<TRow> {
  let viewport = $state.raw<QueryViewport<TRow>>(empty);
  let controller: QueryViewportController<TRow> | undefined;
  let container: HTMLElement | null = null;
  // Registered before the first flush, so a destroyed owner never leaks the controller (see useOperation).
  $effect.pre(() => {
    const current = collection();
    if (!current) { viewport = empty; return; }
    const created = untrack(() => createQueryViewportController(current, options()));
    controller = created;
    const stop = created.subscribe(() => { viewport = created.current; });
    created.attach(container);
    viewport = created.current;
    return () => {
      stop();
      created.dispose();
      if (controller === created) controller = undefined;
    };
  });
  $effect.pre(() => {
    const { estimateSize, overscan } = options();
    untrack(() => controller?.update({ estimateSize, ...(overscan === undefined ? {} : { overscan }) }));
  });
  return {
    get viewport() { return viewport; },
    attach(element) {
      container = element;
      controller?.attach(element);
      return () => { container = null; controller?.attach(null); };
    },
    measure: index => element => controller?.observe(element, index),
    scrollToKey: async (key, align) => controller ? controller.scrollToKey(key, align) : false,
    scrollToIndex: (index, align) => controller?.scrollToIndex(index, align),
    setQuery: async query => {
      const current = untrack(collection);
      if (!current) throw new TypeError("useQueryCollection has no collection yet.");
      await current.setQuery(query);
    },
    retry: () => untrack(collection)?.retry(),
  };
}
