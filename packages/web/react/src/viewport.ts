import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import {
  createCollectionViewportController,
  type CollectionViewport,
  type CollectionViewportOptions,
} from "@runic-artifex/views";

export interface CollectionViewportHandle {
  /** The rows to request and the sizes to render. */
  readonly viewport: CollectionViewport;
  /** Attach to the scroll container: `<div ref={ref}>`. */
  readonly ref: (element: HTMLElement | null) => void;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET. Send `viewport.start` and `viewport.size` to the ViewModel from an
 * effect; the hook only measures.
 */
export function useCollectionViewport(options: CollectionViewportOptions): CollectionViewportHandle {
  const [controller] = useState(() => createCollectionViewportController(options));
  const { totalCount, rowHeight, overscan } = options;
  useEffect(() => {
    controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) });
  }, [controller, totalCount, rowHeight, overscan]);
  const read = useCallback(() => controller.current, [controller]);
  const viewport = useSyncExternalStore(controller.subscribe, read, read);
  const ref = useCallback((element: HTMLElement | null) => controller.attach(element), [controller]);
  return { viewport, ref };
}
