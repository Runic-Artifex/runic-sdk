<script lang="ts">
  import type { RegionCollection, RegionEntry } from "@runic-artifex/views";
  import { RegionOutlets, type RegionPanelAttributes, type ViewRegistry } from "../../src/views/index.js";
  import type { Page } from "./pages.js";
  let { collection, registry, mode, panelProps }: {
    collection: RegionCollection<Page> | undefined;
    registry: ViewRegistry<Page>;
    mode?: "keep-mounted" | "selected";
    panelProps?: (entry: RegionEntry<Page>) => RegionPanelAttributes;
  } = $props();
</script>

<RegionOutlets {collection} {registry} {...mode ? { mode } : {}} {...panelProps ? { panelProps } : {}}>
  {#snippet fallback()}<output>no documents</output>{/snippet}
</RegionOutlets>
