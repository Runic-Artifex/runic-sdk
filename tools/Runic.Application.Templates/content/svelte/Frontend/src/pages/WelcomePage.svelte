<svelte:options runes={true} />

<script lang="ts">
  import { useView } from "@runic-artifex/svelte/views";
  import type { WelcomePageReference } from "../generated/welcome.js";

  let { page }: { page: WelcomePageReference } = $props();
  const welcome = useView(() => page);
</script>

<section>
  <h2>{welcome.state?.greeting ?? "Connecting…"}</h2>
  {#if welcome.error}<p role="alert">{String(welcome.error)}</p>{/if}
</section>
