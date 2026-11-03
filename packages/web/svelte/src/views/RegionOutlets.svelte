<script lang="ts" generics="R extends PageReference">
  import type { Snippet } from "svelte";
  import { regionEntries, type PageReference, type RegionCollection, type RegionEntry } from "@runic-artifex/views";
  import type { RegionPanelAttributes } from "./region-panel.js";
  import ViewOutlet from "./ViewOutlet.svelte";
  import type { ViewRegistry } from "./view-registry.js";

  let { collection, registry, fallback, missing, mode = "keep-mounted", panelProps }: {
    /** The presented region collection, such as `state.documents`. */
    collection: RegionCollection<R> | null | undefined;
    registry: ViewRegistry<R>;
    /** Rendered while the collection has no region. */
    fallback?: Snippet;
    /** Renders a reference whose kind has no registered component, as in `ViewOutlet`. */
    missing?: Snippet<[R]>;
    /**
     * `"keep-mounted"` (the default) renders every region and hides all but the selected one, so each region keeps
     * its component and DOM. `"selected"` renders only the selected region.
     */
    mode?: "keep-mounted" | "selected";
    /**
     * Returns attributes for a region's wrapper, for example `{ id, role: "tabpanel", "aria-labelledby" }`, so a tab
     * can point `aria-controls` at the panel.
     */
    panelProps?: (entry: RegionEntry<R>) => RegionPanelAttributes;
  } = $props();
  let shown = $derived(regionEntries(collection).filter(region => mode === "keep-mounted" || region.selected));
</script>

{#each shown as region (region.key)}
  <!-- A hidden region also gets an inline display: none, which author CSS without !important cannot override. -->
  <div {...panelProps?.(region)} data-region-key={region.key} hidden={!region.selected} style:display={region.selected ? undefined : "none"}>
    <ViewOutlet content={region.content} {registry} {...missing ? { missing } : {}} />
  </div>
{:else}
  {@render fallback?.()}
{/each}
