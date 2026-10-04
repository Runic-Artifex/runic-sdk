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
