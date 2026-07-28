<script setup lang="ts">
import { computed } from "vue";
import { useVueMvvm } from "@webuitoolkit/mvvm-vue";
import type { CounterContract } from "./counter-contract.g";
import { useCounterBindings } from "./counter-bindings.g";

const { contract } = defineProps<{ contract: CounterContract }>();
const adapter = useVueMvvm();
const bindings = useCounterBindings(contract, adapter);
const snapshot = adapter.state;
const connected = computed(() => snapshot.value.synchronized);
</script>

<template>
  <main class="container py-5" style="max-width: 720px">
    <div class="d-flex justify-content-between align-items-center mb-3">
      <span class="badge text-bg-success">Vue + native C#</span>
      <span class="badge" :class="connected ? 'text-bg-success' : 'text-bg-secondary'">
        {{ connected ? `Connected · r${snapshot.revision}` : snapshot.phase }}
      </span>
    </div>
    <section class="card border-0 shadow"><div class="card-body p-5">
      <p class="display-2 fw-semibold mb-2">{{ bindings.count.value ?? 0 }}</p>
      <p class="lead text-secondary">{{ bindings.summary.value }}</p>
      <label class="form-label" for="step">Increment step</label>
      <input id="step" type="number" min="1" max="10" class="form-control"
        :class="{ 'is-invalid': bindings.stepErrors.value?.length }"
        :value="bindings.step.value ?? 1"
        @input="contract.step.set(($event.currentTarget as HTMLInputElement).valueAsNumber)">
      <div v-if="bindings.stepErrors.value?.length" class="invalid-feedback">
        {{ bindings.stepErrors.value.join(" ") }}
      </div>
      <button class="btn btn-primary w-100 mt-3"
        :disabled="!bindings.increment.canExecute.value || bindings.increment.isRunning.value"
        @click="bindings.increment.execute()">
        <i class="fa-solid fa-plus me-2" aria-hidden="true" />Increment in C#
      </button>
      <h2 class="h6 mt-4">History</h2>
      <div class="d-flex flex-wrap gap-2">
        <span v-for="(value, index) in bindings.history.value" :key="index"
          class="badge text-bg-light">{{ value }}</span>
      </div>
    </div></section>
  </main>
</template>
