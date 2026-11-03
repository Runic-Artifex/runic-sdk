import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import {
  createQueryViewportController,
  type QueryCollection,
  type QueryViewport,
  type QueryViewportOptions,
  type RunicQuery,
} from "@runic-artifex/views";

export interface QueryCollectionHandle<TRow> extends QueryViewport<TRow> {
  /** Attach to the scroll container: `<div ref={ref}>`. */
  readonly ref: (element: HTMLElement | null) => void;
  /** Attach to each rendered row, so its height is measured: `<div ref={measure(item.index)}>`. */
  readonly measure: (index: number) => (element: Element | null) => void;
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
 * from .NET, with placeholders until they arrive, and lays them out with variable heights. Render `items` absolutely
 * at `top` inside a spacer of `totalSize`, and attach `measure(item.index)` to each row.
 */
export function useQueryCollection<TRow>(collection: QueryCollection<TRow>, options: QueryViewportOptions): QueryCollectionHandle<TRow> {
  // The controller follows the collection while it has subscribers, so the hook does not dispose it.
  const controller = useMemo(() => createQueryViewportController(collection, options), [collection]);
  const { estimateSize, overscan } = options;
  useEffect(() => {
    controller.update({ estimateSize, ...(overscan === undefined ? {} : { overscan }) });
  }, [controller, estimateSize, overscan]);
  const read = useCallback(() => controller.current, [controller]);
  const viewport = useSyncExternalStore(controller.subscribe, read, read);
  const ref = useCallback((element: HTMLElement | null) => controller.attach(element), [controller]);
  const observed = useRef(new Map<number, () => void>());
  const measure = useCallback((index: number) => (element: Element | null) => {
    observed.current.get(index)?.();
    observed.current.delete(index);
    if (element) observed.current.set(index, controller.observe(element, index));
  }, [controller]);
  return {
    ...viewport, ref, measure,
    scrollToKey: (key, align) => controller.scrollToKey(key, align),
    scrollToIndex: (index, align) => controller.scrollToIndex(index, align),
    setQuery: query => collection.setQuery(query),
    retry: () => collection.retry(),
  };
}
