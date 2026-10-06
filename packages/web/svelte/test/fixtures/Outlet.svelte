<script lang="ts">
  import { ViewOutlet, type ViewRegistry } from "../../src/views/index.js";
  import type { Page } from "./pages.js";
  let { content, registry }: { content: Page | undefined; registry: ViewRegistry<Page> } = $props();
</script>

<ViewOutlet {content} {registry}>
  {#snippet fallback()}<output>empty</output>{/snippet}
</ViewOutlet>
