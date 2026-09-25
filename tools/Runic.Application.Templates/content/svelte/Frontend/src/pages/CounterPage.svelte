<svelte:options runes={true} />

<script lang="ts">
  import { onMount } from "svelte";
  import type { CounterPageReference, CounterState, CounterView } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  let viewState = $state.raw<CounterState | undefined>(undefined);
  let error = $state<string | undefined>(undefined);
  let view = $state.raw<CounterView | undefined>(undefined);
  let unsubscribe = () => {};

  onMount(() => {
    let active = true;
    void page.connect().then(client => {
      if (!active) { client.dispose(); return; }
      view = client;
      unsubscribe = client.subscribe(next => { viewState = next; });
    }).catch(cause => { error = String(cause); });
    return () => { active = false; unsubscribe(); view?.dispose(); };
  });

  async function increment() {
    try { await view?.increment(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<section>
  <h2>Counter View</h2>
  <p class="count">{viewState?.count ?? "…"}</p>
  <button disabled={!view} onclick={increment}>Increment</button>
  {#if error}<p role="alert">{error}</p>{/if}
</section>
