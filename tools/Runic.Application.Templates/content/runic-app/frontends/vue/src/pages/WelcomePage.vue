<script setup lang="ts">
import { useView } from "@runic-artifex/vue";
import { describeBridgeError } from "@runic-artifex/views";
import type { WelcomePageReference } from "../generated/welcome.js";

const props = defineProps<{ page: WelcomePageReference }>();
const { state, error } = useView(() => props.page);
</script>

<template>
  <section><h2>{{ state?.greeting ?? "Connecting…" }}</h2><p v-if="error !== undefined" role="alert">{{ describeBridgeError(error) }}</p></section>
</template>
