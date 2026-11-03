/// <reference lib="esnext.disposable" preserve="true" />
// The viewport of a query collection with variable row heights: rows are laid out from measured sizes, and an estimate
// for rows not measured yet. Sizes live in a Fenwick tree, so a list of 500,000 rows finds the row at a scroll offset
// and the offset of a row in O(log n), and keeps one number per row.
import { disposable } from "./lifetime.js";
import type { QueryCollection, QueryCollectionQuery, QueryRow } from "./query.js";
import type { BridgeError } from "./errors.js";

/** The size of every row of a list, measured or estimated. */
export interface RowSizes {
  /** The number of rows. */
  readonly count: number;
  /** The sum of all sizes. */
  readonly totalSize: number;
  /** The offset of row `index`: the sum of the sizes before it. `offsetOf(count)` is `totalSize`. */
  offsetOf(index: number): number;
  /** The size of row `index`. */
  sizeOf(index: number): number;
  /** The row at `offset`: the last row whose offset is at most `offset`, clamped to the list. */
  indexAt(offset: number): number;
  /** Sets the measured size of row `index`; returns the change of its size. */
  measure(index: number, size: number): number;
  /** Changes the number of rows, keeping the sizes of the rows that stay. */
  resize(count: number): void;
  /** Forgets every measurement. */
  reset(): void;
}

/** An estimated row size: a number, or one per row. */
export type RowSizeEstimate = number | ((index: number) => number);

const checkSize = (size: number) => {
  if (!Number.isFinite(size) || size < 0) throw new RangeError("A row size is a non-negative finite number.");
  return size;
};

/**
 * Creates the sizes of `count` rows, each `estimate` until measured. The framework-neutral half of
 * {@link createQueryViewportController}; a list without a query collection can use it with any scroll container.
 */
export function createRowSizes(count: number, estimate: RowSizeEstimate): RowSizes {
  if (!Number.isSafeInteger(count) || count < 0) throw new RangeError("A row count is a non-negative integer.");
  const estimated = (index: number) => checkSize(typeof estimate === "number" ? estimate : estimate(index));
  let sizes = new Float64Array(0);
  let tree = new Float64Array(1);
  let measured = new Uint8Array(0);
  let rows = 0;
  let total = 0;

  function build(next: number, keep: number): void {
    const oldSizes = sizes;
    const oldMeasured = measured;
    sizes = new Float64Array(next);
    measured = new Uint8Array(next);
    tree = new Float64Array(next + 1);
    total = 0;
    for (let index = 0; index < next; index++) {
      const kept = index < keep && oldMeasured[index] === 1;
      sizes[index] = kept ? oldSizes[index]! : estimated(index);
      measured[index] = kept ? 1 : 0;
      total += sizes[index]!;
      tree[index + 1] = sizes[index]!;
    }
    // Linear construction: each node adds itself to its parent.
    for (let node = 1; node <= next; node++) {
      const parent = node + (node & -node);
      if (parent <= next) tree[parent] = tree[parent]! + tree[node]!;
    }
    rows = next;
  }

  const prefix = (index: number) => {
    let sum = 0;
    for (let node = index; node > 0; node -= node & -node) sum += tree[node]!;
    return sum;
  };

  build(count, 0);
  return {
    get count() { return rows; },
    get totalSize() { return total; },
    offsetOf(index) {
      if (!Number.isSafeInteger(index) || index < 0 || index > rows) throw new RangeError("A row index is in the list.");
      return prefix(index);
    },
    sizeOf(index) {
      if (!Number.isSafeInteger(index) || index < 0 || index >= rows) throw new RangeError("A row index is in the list.");
      return sizes[index]!;
    },
    indexAt(offset) {
      if (rows === 0) return 0;
      if (!(offset > 0)) return 0;
      // Binary lifting: the largest node position whose prefix stays at most offset.
      let position = 0;
      let remaining = offset;
      for (let step = 1 << Math.floor(Math.log2(rows)); step > 0; step >>= 1) {
        const next = position + step;
        if (next <= rows && tree[next]! <= remaining) {
          position = next;
          remaining -= tree[next]!;
        }
      }
      return Math.min(position, rows - 1);
    },
    measure(index, size) {
      if (!Number.isSafeInteger(index) || index < 0 || index >= rows) return 0;
      checkSize(size);
      const delta = size - sizes[index]!;
      measured[index] = 1;
      if (delta === 0) return 0;
      sizes[index] = size;
      total += delta;
      for (let node = index + 1; node <= rows; node += node & -node) tree[node] = tree[node]! + delta;
      return delta;
    },
    resize(next) {
      if (!Number.isSafeInteger(next) || next < 0) throw new RangeError("A row count is a non-negative integer.");
      if (next !== rows) build(next, Math.min(rows, next));
    },
    reset() { build(rows, 0); },
  };
}

/** A row of a query viewport, with its layout. */
export interface QueryViewportItem<TRow> extends QueryRow<TRow> {
  /** The row's offset from the top of the list. */
  readonly top: number;
  /** The row's size: measured, or estimated until it is. */
  readonly size: number;
}

/** What a query viewport shows. A new object on every change. */
export interface QueryViewport<TRow> {
  /** The rows to render, with their offsets: the visible rows and the overscan. */
  readonly items: readonly QueryViewportItem<TRow>[];
  /** The height of the scroll spacer. */
  readonly totalSize: number;
  readonly total: number | null;
  readonly query: QueryCollectionQuery;
  readonly loading: boolean;
  readonly error: BridgeError | undefined;
}

export interface QueryViewportOptions {
  /** The estimated size of a row that was not measured, in pixels. */
  readonly estimateSize: RowSizeEstimate;
  /** Rows loaded and rendered before and after the visible ones. Defaults to 5. */
  readonly overscan?: number;
}

/**
 * Follows a scroll container for a query collection: it asks the collection for the rows the container shows and lays
 * them out with variable heights. Bindings wrap it as `useQueryCollection`.
 */
export interface QueryViewportController<TRow> {
  readonly current: QueryViewport<TRow>;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /** Follows `element`'s scroll position and size. Pass `null` to detach. */
  attach(element: HTMLElement | null | undefined): void;
  /**
   * Measures a rendered row element and follows its size until the returned function is called. A row above the
   * visible ones that changes size keeps the visible rows where they are.
   */
  observe(element: Element, index: number): () => void;
  /** Sets the measured size of row `index`. */
  measure(index: number, size: number): void;
  /** Scrolls the container so that row `index` is at its top (`start`), center or bottom (`end`). */
  scrollToIndex(index: number, align?: "start" | "center" | "end"): void;
  /**
   * Scrolls to the row with `key`: asks .NET for its position in the current query (`query-index`), then scrolls to it.
   * Resolves `false` when no row has the key.
   */
  scrollToKey(key: string, align?: "start" | "center" | "end"): Promise<boolean>;
  /** Changes the estimate or overscan. */
  update(options: QueryViewportOptions): void;
  dispose(): void;
}

const emptyQuery: QueryCollectionQuery = { sort: [], filters: [] };

/** Creates the variable-height viewport of a query collection. A `using` declaration disposes it where the engine has `Symbol.dispose`. */
export function createQueryViewportController<TRow>(collection: QueryCollection<TRow>, options: QueryViewportOptions): QueryViewportController<TRow> & Disposable {
  const listeners = new Set<() => void>();
  let settings = options;
  let sizes = createRowSizes(collection.current.total ?? 0, settings.estimateSize);
  let element: HTMLElement | undefined;
  let detach: (() => void) | undefined;
  let disposed = false;
  let queryText = JSON.stringify(collection.current.query);
  const observed = new Map<Element, number>();
  const resizeObserver = typeof ResizeObserver === "function" ? new ResizeObserver(entries => {
    let changed = false;
    for (const entry of entries) {
      const index = observed.get(entry.target);
      if (index === undefined) continue;
      const size = entry.borderBoxSize?.[0]?.blockSize ?? entry.contentRect.height;
      changed = apply(index, size) || changed;
    }
    if (changed) compute();
  }) : undefined;

  const overscan = () => {
    const value = settings.overscan ?? 5;
    if (!Number.isSafeInteger(value) || value < 0) throw new RangeError("Overscan is a non-negative integer.");
    return value;
  };

  function layout(): QueryViewport<TRow> {
    const snapshot = collection.current;
    const items: QueryViewportItem<TRow>[] = [];
    for (const row of snapshot.rows) {
      if (row.index >= sizes.count) break;
      items.push({ ...row, top: sizes.offsetOf(row.index), size: sizes.sizeOf(row.index) });
    }
    return { items, totalSize: sizes.totalSize, total: snapshot.total, query: snapshot.query ?? emptyQuery, loading: snapshot.loading, error: snapshot.error };
  }
  let current = layout();

  function publish(): void {
    if (disposed) return;
    current = layout();
    for (const listener of [...listeners]) listener();
  }

  // Sets a measured size; keeps the visible rows in place when a row above them changes. Returns whether it changed.
  function apply(index: number, size: number): boolean {
    if (index >= sizes.count) return false;
    const before = element ? sizes.indexAt(element.scrollTop) : 0;
    const delta = sizes.measure(index, size);
    if (delta === 0) return false;
    if (element && index < before) element.scrollTop += delta;
    return true;
  }

  // Asks the collection for the rows the container shows.
  function compute(): void {
    if (disposed) return;
    const total = collection.current.total ?? 0;
    if (total !== sizes.count) sizes.resize(total);
    const top = element?.scrollTop ?? 0;
    const height = element?.clientHeight ?? 0;
    const first = sizes.indexAt(Math.max(0, top));
    const last = sizes.indexAt(top + height);
    const start = Math.max(0, first - overscan());
    // Before the total is known, the first rows load so it becomes known.
    const end = total === 0 ? Math.max(1, Math.ceil(height / Math.max(1, sizes.count === 0 ? estimate(0) : sizes.sizeOf(0)))) + overscan()
      : Math.min(total, last + 1 + overscan());
    collection.setRange(start, Math.max(0, end - start));
    publish();
  }

  const estimate = (index: number) => typeof settings.estimateSize === "number" ? settings.estimateSize : settings.estimateSize(index);

  // The collection is followed while the controller has subscribers, so a binding that never disposes it leaks nothing.
  const follow = () => {
    const snapshot = collection.current;
    // Rows of another query have other sizes.
    const text = JSON.stringify(snapshot.query);
    if (text !== queryText) { queryText = text; sizes.reset(); }
    if ((snapshot.total ?? 0) !== sizes.count) { compute(); return; }
    publish();
  };
  let stop: (() => void) | undefined;

  function scrollTo(index: number, align: "start" | "center" | "end"): void {
    if (!Number.isSafeInteger(index) || index < 0) throw new RangeError("A row index is a non-negative integer.");
    if (index >= sizes.count) return;
    const top = sizes.offsetOf(index);
    const height = element?.clientHeight ?? 0;
    const offset = align === "start" ? top : align === "end" ? top + sizes.sizeOf(index) - height : top + sizes.sizeOf(index) / 2 - height / 2;
    if (element) element.scrollTop = Math.max(0, offset);
    compute();
  }

  return disposable<QueryViewportController<TRow>>({
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      if (!disposed && stop === undefined) {
        stop = collection.subscribe(follow);
        // What changed while nobody followed.
        if ((collection.current.total ?? 0) !== sizes.count || JSON.stringify(collection.current.query) !== queryText) follow();
      }
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) { stop?.(); stop = undefined; }
      };
    },
    attach(target) {
      if (disposed || (target ?? undefined) === element) return;
      detach?.();
      detach = undefined;
      element = target ?? undefined;
      if (element) {
        const container = element;
        let frame: number | undefined;
        const schedule = () => {
          if (typeof requestAnimationFrame !== "function") { compute(); return; }
          frame ??= requestAnimationFrame(() => { frame = undefined; compute(); });
        };
        container.addEventListener("scroll", schedule, { passive: true });
        const resize = typeof ResizeObserver === "function" ? new ResizeObserver(() => compute()) : undefined;
        resize?.observe(container);
        detach = () => {
          container.removeEventListener("scroll", schedule);
          resize?.disconnect();
          if (frame !== undefined) cancelAnimationFrame(frame);
        };
      }
      compute();
    },
    observe(target, index) {
      if (disposed) return () => {};
      observed.set(target, index);
      resizeObserver?.observe(target);
      // Measured at once where layout is available, so the first frame is placed correctly.
      const size = (target as HTMLElement).getBoundingClientRect?.().height;
      if (size !== undefined && size > 0 && apply(index, size)) compute();
      return () => {
        if (observed.get(target) !== index) return;
        observed.delete(target);
        resizeObserver?.unobserve(target);
      };
    },
    measure(index, size) {
      if (apply(index, size)) compute();
    },
    scrollToIndex(index, align = "start") { scrollTo(index, align); },
    async scrollToKey(key, align = "start") {
      const index = await collection.indexOf(key);
      if (index === undefined || disposed) return false;
      // The total can arrive with the position: the list must be long enough to reach it.
      if (index >= sizes.count) sizes.resize(Math.max(index + 1, collection.current.total ?? 0));
      scrollTo(index, align);
      return true;
    },
    update(next) {
      const estimateChanged = next.estimateSize !== settings.estimateSize;
      settings = next;
      if (estimateChanged) sizes = createRowSizes(sizes.count, settings.estimateSize);
      compute();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      detach?.();
      detach = undefined;
      element = undefined;
      stop?.();
      stop = undefined;
      resizeObserver?.disconnect();
      observed.clear();
      listeners.clear();
    },
  });
}
