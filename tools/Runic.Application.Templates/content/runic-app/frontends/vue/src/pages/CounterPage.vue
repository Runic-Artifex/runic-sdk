<script setup lang="ts">
import { computed } from "vue";
import { useCommand, useView } from "@runic-artifex/vue";
import type { CounterPageReference } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const { run: increment, pending: incrementing, error: incrementError } = useCommand(() => client.value?.increment());
// Step has a public C# setter, so the client has setStep for v-model.
// Count's setter is private: the page only reads it.
const { run: setStep, error: stepError } = useCommand((value: number) => client.value?.setStep(value));
const step = computed({ get: () => state.value?.step ?? 1, set: value => void setStep(value) });
const error = computed(() => incrementError.value ?? stepError.value ?? connection.value);
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
    <button :disabled="!client || incrementing" @click="increment()">Increment</button>
    <p v-if="error !== undefined" role="alert">{{ String(error) }}</p>
  </section>
</template>
