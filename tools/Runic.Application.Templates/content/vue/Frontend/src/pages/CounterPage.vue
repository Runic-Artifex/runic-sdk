<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import type { CounterPageReference, CounterState, CounterView } from "../generated/counter.js";

const props = defineProps<{ page: CounterPageReference }>();
const view = ref<CounterView>();
const state = ref<CounterState>();
const error = ref<string>();
let unsubscribe = () => {};
let active = true;

onMounted(() => {
  void props.page.connect().then(client => {
    if (!active) { client.dispose(); return; }
    view.value = client;
    unsubscribe = client.subscribe(next => { state.value = next; });
  }).catch(cause => { error.value = String(cause); });
});
onUnmounted(() => { active = false; unsubscribe(); view.value?.dispose(); });

async function increment() {
  try { await view.value?.increment(); error.value = undefined; }
  catch (cause) { error.value = String(cause); }
}
</script>

<template>
  <section>
    <h2>Counter View</h2>
    <p class="count">{{ state?.count ?? "…" }}</p>
    <button :disabled="!view" @click="increment">Increment</button>
    <p v-if="error" role="alert">{{ error }}</p>
  </section>
</template>
