<script setup lang="ts">
import { useView } from "@runic-artifex/vue";
import type { WelcomePageReference } from "../generated/welcome.js";

const props = defineProps<{ page: WelcomePageReference }>();
const { state, error } = useView(() => props.page);
</script>

<template>
  <section><h2>{{ state?.greeting ?? "Connecting…" }}</h2><p v-if="error" role="alert">{{ error }}</p></section>
</template>
