<script lang="ts">
  import { ViewOutlet, type ViewRegistry } from "../../src/views/index.js";
  import type { Page } from "./pages.js";
  let { content, registry }: { content: Page | undefined; registry: ViewRegistry<Page> } = $props();
</script>

<ViewOutlet {content} {registry}>
  {#snippet missing(page)}<output>Keine Ansicht für {page.kind}</output>{/snippet}
</ViewOutlet>
