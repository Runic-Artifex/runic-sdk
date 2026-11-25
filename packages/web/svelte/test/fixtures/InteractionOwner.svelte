<script lang="ts">
  import { useInteraction } from "../../src/views/index.js";
  import type { DiscardClient } from "../interactions.js";
  let { client }: { client: DiscardClient | undefined } = $props();
  export const discard = useInteraction(() => ({ client }), "confirmDiscard");
</script>

{#if discard.request}<p id="prompt">{discard.request.title}</p>{/if}
