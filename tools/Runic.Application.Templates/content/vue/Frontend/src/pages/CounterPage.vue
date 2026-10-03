<script setup lang="ts">
import { ref } from "vue";
import { useView } from "@runic-artifex/vue";
import type { CounterPageReference } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const error = ref<string>();

async function increment() {
  try { await client.value?.increment(); error.value = undefined; }
  catch (cause) { error.value = String(cause); }
}
</script>

<template>
  <section>
    <h2>Counter View</h2>
    <p class="count">{{ state?.count ?? "…" }}</p>
    <button :disabled="!client" @click="increment">Increment</button>
    <p v-if="error ?? connection" role="alert">{{ error ?? String(connection) }}</p>
  </section>
</template>
