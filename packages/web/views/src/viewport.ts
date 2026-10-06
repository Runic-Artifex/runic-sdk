export interface CollectionViewport {
  /** First row requested from .NET, including overscan. */
  readonly start: number;
  /** Number of rows requested from .NET. */
  readonly size: number;
  /** Pixel offset of the bounded row container. */
  readonly offset: number;
  /** Height of the scrollable spacer. */
  readonly totalSize: number;
}

/** Computes a bounded viewport for fixed-height rows in any web framework. */
export function collectionViewport(options: {
  readonly totalCount: number;
  readonly scrollTop: number;
  readonly height: number;
  readonly rowHeight: number;
  readonly overscan?: number;
}): CollectionViewport {
  const { totalCount, scrollTop, height, rowHeight, overscan = 5 } = options;
  if (!Number.isSafeInteger(totalCount) || totalCount < 0 || !Number.isSafeInteger(overscan) || overscan < 0 ||
    !Number.isFinite(scrollTop) || !Number.isFinite(height) || height < 0 || !Number.isFinite(rowHeight) || rowHeight <= 0)
    throw new RangeError("Invalid collection viewport.");
  const first = Math.min(totalCount, Math.floor(Math.max(0, scrollTop) / rowHeight));
  const start = Math.max(0, first - overscan);
  const end = Math.min(totalCount, first + Math.ceil(height / rowHeight) + overscan);
  return { start, size: end - start, offset: start * rowHeight, totalSize: totalCount * rowHeight };
}

/** The list a viewport controller measures: its row count, fixed row height and overscan. */
export interface CollectionViewportOptions {
  readonly totalCount: number;
  readonly rowHeight: number;
  /** Rows requested before and after the visible range. Defaults to 5. */
  readonly overscan?: number;
}

/**
 * Follows a scroll container and publishes its {@link CollectionViewport}.
 * Framework bindings attach their element and pass the current row count.
 */
export interface CollectionViewportController {
  readonly current: CollectionViewport;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /** Measures `element` and follows its scroll position and size. Pass `null` to detach. */
  attach(element: HTMLElement | null | undefined): void;
  /** Applies a new row count, row height or overscan. */
  update(options: CollectionViewportOptions): void;
  /** Detaches and stops publishing. */
  dispose(): void;
}

const sameViewport = (left: CollectionViewport, right: CollectionViewport) =>
  left.start === right.start && left.size === right.size && left.offset === right.offset && left.totalSize === right.totalSize;

/**
 * Creates the framework-neutral viewport tracker behind each binding's
 * `useCollectionViewport`. Scroll events are coalesced to one measurement per
 * animation frame, and a `ResizeObserver` follows the container's height.
 * `current` only changes when the requested range or sizes change.
 */
export function createCollectionViewportController(options: CollectionViewportOptions): CollectionViewportController {
  const listeners = new Set<() => void>();
  let settings = options;
  let element: HTMLElement | undefined;
  let detach: (() => void) | undefined;
  let disposed = false;
  const compute = () => collectionViewport({
    totalCount: settings.totalCount, rowHeight: settings.rowHeight, ...(settings.overscan === undefined ? {} : { overscan: settings.overscan }),
    scrollTop: element?.scrollTop ?? 0, height: element?.clientHeight ?? 0,
  });
  let current = compute();

  function measure(): void {
    if (disposed) return;
    const next = compute();
    if (sameViewport(next, current)) return;
    current = next;
    for (const listener of [...listeners]) listener();
  }

  return {
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    attach(target) {
      if (disposed || (target ?? undefined) === element) return;
      detach?.();
      detach = undefined;
      element = target ?? undefined;
      if (element) {
        const observed = element;
        let frame: number | undefined;
        const schedule = () => {
          if (typeof requestAnimationFrame !== "function") { measure(); return; }
          frame ??= requestAnimationFrame(() => { frame = undefined; measure(); });
        };
        observed.addEventListener("scroll", schedule, { passive: true });
        const resize = typeof ResizeObserver === "function" ? new ResizeObserver(() => measure()) : undefined;
        resize?.observe(observed);
        detach = () => {
          observed.removeEventListener("scroll", schedule);
          resize?.disconnect();
          if (frame !== undefined) cancelAnimationFrame(frame);
        };
      }
      measure();
    },
    update(next) {
      settings = next;
      measure();
    },
    dispose() {
      if (disposed) return;
      detach?.();
      detach = undefined;
      element = undefined;
      disposed = true;
      listeners.clear();
    },
  };
}
