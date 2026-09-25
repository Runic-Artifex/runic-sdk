<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import type { WelcomePageReference, WelcomeState, WelcomeView } from "../generated/welcome.js";

const props = defineProps<{ page: WelcomePageReference }>();
const state = ref<WelcomeState>();
const error = ref<string>();
let client: WelcomeView | undefined;
let unsubscribe = () => {};
let active = true;

onMounted(() => {
  void props.page.connect().then(connected => {
    if (!active) { connected.dispose(); return; }
    client = connected;
    unsubscribe = connected.subscribe(next => { state.value = next; });
  }).catch(cause => { error.value = String(cause); });
});
onUnmounted(() => { active = false; unsubscribe(); client?.dispose(); });
</script>

<template>
  <section><h2>{{ state?.greeting ?? "Connecting…" }}</h2><p v-if="error" role="alert">{{ error }}</p></section>
</template>
