<script lang="ts">
  import type { SvelteMvvmApplication } from "@webuitoolkit/mvvm-svelte";
  import { untrack } from "svelte";
  import {
    provideSvelteMvvmApplication,
  } from "@webuitoolkit/mvvm-svelte";
  import type { CounterContract } from "./counter-contract.g";
  import { createCounterStores } from "./counter-bindings.g";

  let { application }: {
    application: SvelteMvvmApplication<CounterContract>;
  } = $props();
  const initialApplication = untrack(() => application);
  provideSvelteMvvmApplication(initialApplication);
  const store = initialApplication.store;
  const stores = createCounterStores(store, initialApplication.contract);
  const { count, step, summary, stepErrors, history, increment } = stores;
  initialApplication.addCleanup(() => stores.dispose());
</script>

<main class="container py-5" style="max-width: 720px">
  <div class="d-flex justify-content-between align-items-center mb-3">
    <span class="badge text-bg-danger">Svelte + native C#</span>
    <span class="badge" class:text-bg-success={$store.synchronized}
      class:text-bg-secondary={!$store.synchronized}>
      {$store.synchronized ? `Connected · r${$store.revision}` : $store.phase}
    </span>
  </div>
  <section class="card border-0 shadow"><div class="card-body p-5">
    <p class="display-2 fw-semibold mb-2">{$count ?? 0}</p>
    <p class="lead text-secondary">{$summary}</p>
    <label class="form-label" for="step">Increment step</label>
    <input id="step" type="number" min="1" max="10"
      class:form-control={true} class:is-invalid={$stepErrors.length > 0}
      value={$step ?? 1}
      oninput={(event) =>
        initialApplication.contract.step.set((event.currentTarget as HTMLInputElement).valueAsNumber)}>
    {#if $stepErrors.length}<div class="invalid-feedback">{$stepErrors.join(" ")}</div>{/if}
    <button class="btn btn-primary w-100 mt-3"
      disabled={!$increment.canExecute || $increment.isRunning}
      onclick={() => increment.execute()}>
      <i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Increment in C#
    </button>
    <h2 class="h6 mt-4">History</h2>
    <div class="d-flex flex-wrap gap-2">
      {#each $history as value, index (index)}
        <span class="badge text-bg-light">{value}</span>
      {/each}
    </div>
  </div></section>
</main>
