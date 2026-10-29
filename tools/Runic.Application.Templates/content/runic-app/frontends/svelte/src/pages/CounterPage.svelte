<svelte:options runes={true} />

<script lang="ts">
  import { useCommand, useField, useView } from "@runic-artifex/svelte";
  import { CounterFields, type CounterPageReference } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  const increment = useCommand(() => counter.client?.increment());
  // Step and Label have public C# setters, so the generated CounterFields has a
  // field for each; the field's attachment writes them and keeps what the user
  // typed while the writes are on their way. Count's setter is private: the page
  // only reads it.
  const step = useField(() => counter, CounterFields.step);
  const label = useField(() => counter, CounterFields.label);
  const error = $derived(increment.error ?? step.error ?? label.error ?? counter.error);
</script>

<section>
  <h2>Counter View</h2>
  <p class="caption">{counter.state?.label ?? "…"}</p>
  <p class="count">{counter.state?.count ?? "…"}</p>
  <label>
    Step
    <select disabled={!counter.client} {@attach step.attach}>
      {#each [1, 2, 5, 10] as value (value)}<option value={value}>{value}</option>{/each}
    </select>
  </label>
  <label>
    Label
    <input disabled={!counter.client} {@attach label.attach}>
  </label>
  <!-- Increment waits until the Step it adds has been written. -->
  <button disabled={!counter.client || increment.pending || step.editing} onclick={() => increment.run()}>Increment</button>
  {#if error !== undefined}<p role="alert">{String(error)}</p>{/if}
</section>
