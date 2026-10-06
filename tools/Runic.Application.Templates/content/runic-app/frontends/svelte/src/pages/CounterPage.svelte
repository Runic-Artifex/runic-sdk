<svelte:options runes={true} />

<script lang="ts">
  import { useCommand, useView } from "@runic-artifex/svelte/views";
  import type { CounterPageReference } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  const increment = useCommand(() => counter.client?.increment());
  const error = $derived(increment.error ?? counter.error);
</script>

<section>
  <h2>Counter View</h2>
  <p class="count">{counter.state?.count ?? "…"}</p>
  <button disabled={!counter.client || increment.pending} onclick={() => increment.run()}>Increment</button>
  {#if error !== undefined}<p role="alert">{String(error)}</p>{/if}
</section>
