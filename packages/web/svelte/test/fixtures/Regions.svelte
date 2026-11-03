<script lang="ts">
  import type { RegionCollection } from "@runic-artifex/views";
  import { RegionOutlets, type ViewRegistry } from "../../src/views/index.js";
  import type { Page } from "./pages.js";
  let { collection, registry, mode }: {
    collection: RegionCollection<Page> | undefined;
    registry: ViewRegistry<Page>;
    mode?: "keep-mounted" | "selected";
  } = $props();
</script>

<RegionOutlets {collection} {registry} {...mode ? { mode } : {}}>
  {#snippet fallback()}<output>no documents</output>{/snippet}
</RegionOutlets>
