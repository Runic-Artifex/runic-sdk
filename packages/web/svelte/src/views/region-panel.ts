import type { HTMLAttributes } from "svelte/elements";

/**
 * Attributes that `RegionOutlets` merges onto a region's wrapper, such as `id`, `role` and `aria-labelledby`.
 * `hidden` and `data-region-key` stay controlled by the outlet, and its `display: none` takes precedence over `style`.
 */
export type RegionPanelAttributes = Omit<HTMLAttributes<HTMLDivElement>, "hidden" | "children">;
