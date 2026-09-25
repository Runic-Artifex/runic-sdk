<svelte:options runes={true} />

<script lang="ts">
  import { onMount } from "svelte";
  import type { WelcomePageReference, WelcomeState, WelcomeView } from "../generated/welcome.js";

  let { page }: { page: WelcomePageReference } = $props();
  let viewState = $state.raw<WelcomeState | undefined>(undefined);
  let error = $state<string | undefined>(undefined);
  let client = $state.raw<WelcomeView | undefined>(undefined);
  let unsubscribe = () => {};

  onMount(() => {
    let active = true;
    void page.connect().then(connected => {
      if (!active) { connected.dispose(); return; }
      client = connected;
      unsubscribe = connected.subscribe(next => { viewState = next; });
    }).catch(cause => { error = String(cause); });
    return () => { active = false; unsubscribe(); client?.dispose(); };
  });
</script>

<section>
  <h2>{viewState?.greeting ?? "Connecting…"}</h2>
  {#if error}<p role="alert">{error}</p>{/if}
</section>
