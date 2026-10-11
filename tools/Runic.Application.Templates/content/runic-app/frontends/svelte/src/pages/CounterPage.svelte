<svelte:options runes={true} />

<script lang="ts">
  import { useCommand, useView } from "@runic-artifex/svelte/views";
  import type { CounterPageReference } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  const increment = useCommand(() => counter.client?.increment());
  // Step has a public C# setter, so the client has setStep for a two-way
  // binding. Count's setter is private: the page only reads it.
  const setStep = useCommand((step: number) => counter.client?.setStep(step));
  const error = $derived(increment.error ?? setStep.error ?? counter.error);
</script>

<section>
  <h2>Counter View</h2>
  <p class="count">{counter.state?.count ?? "…"}</p>
  <label>
    Step
    <select disabled={!counter.client} bind:value={() => counter.state?.step ?? 1, step => setStep.run(step)}>
      {#each [1, 2, 5, 10] as step (step)}<option value={step}>{step}</option>{/each}
    </select>
  </label>
  <button disabled={!counter.client || increment.pending} onclick={() => increment.run()}>Increment</button>
  {#if error !== undefined}<p role="alert">{String(error)}</p>{/if}
</section>
