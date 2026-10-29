<script setup lang="ts">
import { computed } from "vue";
import { useCommand, useField, useView } from "@runic-artifex/vue";
import { CounterFields, type CounterPageReference } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const { run: increment, pending: incrementing, error: incrementError } = useCommand(() => client.value?.increment());
// Step and Label have public C# setters, so the generated CounterFields has a
// field for each; useField writes them through v-model and keeps what the user
// typed while the writes are on their way. Count's setter is private: the page
// only reads it.
const step = useField({ state, client }, CounterFields.step);
const label = useField({ state, client }, CounterFields.label);
const error = computed(() => incrementError.value ?? step.error ?? label.error ?? connection.value);
</script>

<template>
  <section>
    <h2>Counter View</h2>
    <p class="caption">{{ state?.label ?? "…" }}</p>
    <p class="count">{{ state?.count ?? "…" }}</p>
    <label>
      Step
      <select v-model="step.value" :disabled="!client" @blur="step.commit">
        <option v-for="value in [1, 2, 5, 10]" :key="value" :value="value">{{ value }}</option>
      </select>
    </label>
    <label>
      Label
      <input v-model="label.value" :disabled="!client" @blur="label.commit">
    </label>
    <!-- Increment waits until the Step it adds has been written. -->
    <button :disabled="!client || incrementing || step.editing" @click="increment()">Increment</button>
    <p v-if="error !== undefined" role="alert">{{ String(error) }}</p>
  </section>
</template>
