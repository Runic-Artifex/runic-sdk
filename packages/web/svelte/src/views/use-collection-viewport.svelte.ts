import { untrack } from "svelte";
import type { Attachment } from "svelte/attachments";
import { createCollectionViewportController, type CollectionViewport, type CollectionViewportOptions } from "@runic-artifex/views";

export interface CollectionViewportHandle {
  /** The rows to request and the sizes to render. */
  readonly viewport: CollectionViewport;
  /** Attach to the scroll container: `<div {@attach list.attach}>`. */
  readonly attach: Attachment<HTMLElement>;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET. Call it during component initialization with a getter, such as
 * `useCollectionViewport(() => ({ totalCount: rows.state?.totalCount ?? 0, rowHeight: 32 }))`,
 * and send `viewport.start` and `viewport.size` to the ViewModel from an effect.
 */
export function useCollectionViewport(options: () => CollectionViewportOptions): CollectionViewportHandle {
  const controller = createCollectionViewportController(untrack(options));
  let viewport = $state.raw(controller.current);
  controller.subscribe(() => { viewport = controller.current; });
  $effect.pre(() => {
    const { totalCount, rowHeight, overscan } = options();
    controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) });
  });
  $effect(() => () => controller.dispose());
  return {
    get viewport() { return viewport; },
    attach(element) {
      controller.attach(element);
      return () => controller.attach(null);
    },
  };
}
