<script lang="ts">
  import { counterBridge } from "./counter-bridge";
  import { counterBridgeContext } from "./counter-context.svelte";

  const bridge = counterBridgeContext.provide(counterBridge);
  let snapshot = $derived(bridge.snapshot ?? { count: 0, history: [0], revision: 0 });
  let step = $state(1);
  async function increment(): Promise<void> {
    await bridge.dispatch({ _tag: "IncrementCounter", step });
  }
</script>

<svelte:window onpagehide={() => void bridge.dispose()} />

<main class="container py-5" style="max-width: 720px">
  <div class="d-flex justify-content-between align-items-center mb-3">
    <span class="badge text-bg-danger">Svelte + native C#</span>
    <span class="badge text-bg-success">
      Connected · r{snapshot.revision}
    </span>
  </div>
  <section class="card border-0 shadow"><div class="card-body p-5">
    <p class="display-2 fw-semibold mb-2">{snapshot.count}</p>
    <p class="lead text-secondary">{snapshot.history.length - 1} named command(s)</p>
    <label class="form-label" for="step">Increment step</label>
    <input id="step" type="number" min="1" max="10"
      class="form-control" bind:value={step}>
    {#if bridge.error}<div class="text-danger mt-2">Application Bridge command failed.</div>{/if}
    <button class="btn btn-primary w-100 mt-3"
      onclick={() => void increment()}>
      <i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Increment in C#
    </button>
    <h2 class="h6 mt-4">History</h2>
    <div class="d-flex flex-wrap gap-2">
      {#each snapshot.history as value, index (`${index}-${value}`)}
        <span class="badge text-bg-light">{value}</span>
      {/each}
    </div>
  </div></section>
</main>
