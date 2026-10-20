import { shallowRef, toValue, watch, type ComponentPublicInstance, type MaybeRefOrGetter, type Ref } from "vue";
import {
  createCollectionViewportController,
  type CollectionViewport,
  type CollectionViewportOptions,
} from "@runic-artifex/views/experimental";
import { releaseWithScope } from "./scope.js";

// Experimental (`@runic-artifex/vue/experimental`), like the views viewport controller it wraps.

// Vue's bundler builds require bundlers to define process.env.NODE_ENV too.
declare const process: { readonly env: { readonly NODE_ENV?: string } };

export interface CollectionViewportHandle {
  /** The rows to request and the sizes to render. */
  readonly viewport: Readonly<Ref<CollectionViewport>>;
  /**
   * A function ref for the scroll container, bound to a native element:
   * `<div :ref="attach">`. A component instance or a non-HTML element detaches
   * the viewport (development builds warn); bind a native element instead.
   */
  attach(element: Element | ComponentPublicInstance | null): void;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET. Pass a getter such as `() => ({ totalCount: state.value?.totalCount ?? 0, rowHeight: 32 })`
 * and watch `viewport` to send `start` and `size` to the ViewModel. Call it in
 * `setup()` or `effectScope().run()`: outside an effect scope it is never
 * released (development builds warn).
 */
export function useCollectionViewport(options: MaybeRefOrGetter<CollectionViewportOptions>): CollectionViewportHandle {
  const controller = createCollectionViewportController(toValue(options));
  const viewport = shallowRef(controller.current);
  controller.subscribe(() => { viewport.value = controller.current; });
  watch(() => {
    const { totalCount, rowHeight, overscan } = toValue(options);
    return [totalCount, rowHeight, overscan] as const;
  }, ([totalCount, rowHeight, overscan]) => {
    controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) });
  });
  releaseWithScope("useCollectionViewport", () => controller.dispose());
  let warned = false;
  return {
    viewport,
    attach(element) {
      if (typeof HTMLElement === "undefined") return controller.attach(null);
      const container = element instanceof HTMLElement ? element : null;
      if (process.env.NODE_ENV !== "production" && element !== null && container === null && !warned) {
        warned = true;
        console.warn("useCollectionViewport's attach received a component instance or a non-HTML element, so the viewport " +
          "is detached: bind `:ref=\"attach\"` to a native scroll container such as a <div>.");
      }
      controller.attach(container);
    },
  };
}
