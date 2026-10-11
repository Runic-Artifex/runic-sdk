<script setup lang="ts">
import { computed } from "vue";
import { useCommand, useView } from "@runic-artifex/vue";
import type { CounterPageReference } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const increment = useCommand(() => client.value?.increment());
// Step has a public C# setter, so the client has setStep for v-model.
// Count's setter is private: the page only reads it.
const setStep = useCommand((value: number) => client.value?.setStep(value));
const step = computed({ get: () => state.value?.step ?? 1, set: value => void setStep.run(value) });
const error = computed(() => increment.error ?? setStep.error ?? connection.value);
</script>

<template>
  <section>
    <h2>Counter View</h2>
    <p class="count">{{ state?.count ?? "…" }}</p>
    <label>
      Step
      <select v-model.number="step" :disabled="!client">
        <option v-for="value in [1, 2, 5, 10]" :key="value" :value="value">{{ value }}</option>
      </select>
    </label>
    <button :disabled="!client || increment.pending" @click="increment.run()">Increment</button>
    <p v-if="error !== undefined" role="alert">{{ String(error) }}</p>
  </section>
</template>
