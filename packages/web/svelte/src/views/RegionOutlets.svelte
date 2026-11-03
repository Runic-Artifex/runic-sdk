<script lang="ts" generics="R extends PageReference">
  import type { Snippet } from "svelte";
  import { regionEntries, type PageReference, type RegionCollection } from "@runic-artifex/views";
  import ViewOutlet from "./ViewOutlet.svelte";
  import type { ViewRegistry } from "./view-registry.js";

  let { collection, registry, fallback, missing, mode = "keep-mounted" }: {
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
  } = $props();
  let shown = $derived(regionEntries(collection).filter(region => mode === "keep-mounted" || region.selected));
</script>

{#each shown as region (region.key)}
  <div data-region-key={region.key} hidden={!region.selected}>
    <ViewOutlet content={region.content} {registry} {...missing ? { missing } : {}} />
  </div>
{:else}
  {@render fallback?.()}
{/each}
