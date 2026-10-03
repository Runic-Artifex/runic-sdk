<svelte:options runes={true} />

<script lang="ts">
  import { useView } from "@runic-artifex/svelte/views";
  import type { CounterPageReference } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  let error = $state<string | undefined>(undefined);

  async function increment() {
    try { await counter.client?.increment(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<section>
  <h2>Counter View</h2>
  <p class="count">{counter.state?.count ?? "…"}</p>
  <button disabled={!counter.client} onclick={increment}>Increment</button>
  {#if error ?? counter.error}<p role="alert">{error ?? String(counter.error)}</p>{/if}
</section>
