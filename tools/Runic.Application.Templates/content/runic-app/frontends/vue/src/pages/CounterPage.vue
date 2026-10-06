<script setup lang="ts">
import { useCommand, useView } from "@runic-artifex/vue";
import type { CounterPageReference } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const increment = useCommand(() => client.value?.increment());
</script>

<template>
  <section>
    <h2>Counter View</h2>
    <p class="count">{{ state?.count ?? "…" }}</p>
    <button :disabled="!client || increment.pending" @click="increment.run()">Increment</button>
    <p v-if="(increment.error ?? connection) !== undefined" role="alert">{{ String(increment.error ?? connection) }}</p>
  </section>
</template>
