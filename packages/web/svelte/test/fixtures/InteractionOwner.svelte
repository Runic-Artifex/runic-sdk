<script lang="ts">
  import { useInteraction } from "../../src/views/index.js";
  import type { DiscardClient } from "../interactions.js";
  let { client }: { client: DiscardClient | undefined } = $props();
  export const discard = useInteraction(() => ({ client }), "confirmDiscard");
  export const quit = useInteraction(() => ({ client }), "confirmQuit");
</script>

{#if discard.request}
  {@const request = discard.request}
  <button id="yes" onclick={() => request.respond(true)}>{request.input.title}</button>
{/if}
