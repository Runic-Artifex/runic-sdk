<svelte:options runes={true} />

<script lang="ts">
  import { useText, useView } from "@runic-artifex/svelte";
  import { describeBridgeError } from "@runic-artifex/views";
  import { m } from "../translations";
  import type { WelcomePageReference } from "../generated/welcome.js";

  let { page }: { page: WelcomePageReference } = $props();
  const welcome = useView(() => page);
  // A text reference of the catalog, shown in the window's language.
  const greeting = useText(() => welcome.state?.greeting, () => m.welcome_loading());
</script>

<section>
  <h2>{greeting.current}</h2>
  {#if welcome.error !== undefined}<p role="alert">{describeBridgeError(welcome.error)}</p>{/if}
</section>
