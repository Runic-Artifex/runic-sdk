<svelte:options runes={true} />

<script lang="ts">
  import { useCommand, useView } from "@runic-artifex/svelte";
  import { describeBridgeError } from "@runic-artifex/views";
  import { aboutHeading } from "runic-library-starter";
  import type { AboutPageReference } from "runic-library-starter/generated";

  // The RunicCoreLibrary library's View. Its client, AboutPageReference, and aboutHeading come from the
  // library's frontend package, which the library's build generates and builds.
  let { page }: { page: AboutPageReference } = $props();
  const about = useView(() => page);
  const toggle = useCommand(() => about.client?.toggleDetails());
  const error = $derived(toggle.error ?? about.error);
</script>

<aside class="about">
  <h2>{about.state ? aboutHeading(about.state) : "Connecting…"}</h2>
  {#if about.state?.expanded}<p>Shared ViewModels and Views, with their TypeScript clients in the library's frontend package.</p>{/if}
  <button disabled={!about.client || toggle.pending} onclick={() => toggle.run()}>{about.state?.expanded ? "Hide details" : "Show details"}</button>
  {#if error !== undefined}<p role="alert">{describeBridgeError(error)}</p>{/if}
</aside>
