<svelte:options runes={true} />

<script lang="ts">
  import { useView } from "@runic-artifex/svelte";
  import { describeBridgeError } from "@runic-artifex/views";
  import type { WelcomePageReference } from "../generated/welcome.js";

  let { page }: { page: WelcomePageReference } = $props();
  const welcome = useView(() => page);
</script>

<section>
  <h2>{welcome.state?.greeting ?? "Connecting…"}</h2>
  {#if welcome.error !== undefined}<p role="alert">{describeBridgeError(welcome.error)}</p>{/if}
</section>
