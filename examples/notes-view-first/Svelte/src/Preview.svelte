<script lang="ts">
  import type { PreviewPageReference } from "../../Frontend/src/generated/preview.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: PreviewPageReference } = $props();
  const preview = pageState(() => page);
</script>

{#if preview.state}
  <h2>{preview.state.heading}</h2>
  <p>{preview.state.excerpt}</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if preview.error}<p role="alert">{String(preview.error)}</p>{/if}
