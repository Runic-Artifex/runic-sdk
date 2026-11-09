<script setup lang="ts">
import { useText, useView } from "@runic-artifex/vue";
import { describeBridgeError } from "@runic-artifex/views";
import type { WelcomePageReference } from "../generated/welcome.js";

const props = defineProps<{ page: WelcomePageReference }>();
const { state, error } = useView(() => props.page);
// A text reference of the catalog, shown in the window's language.
const greeting = useText(() => state.value?.greeting, () => "Connecting…");
</script>

<template>
  <section><h2>{{ greeting }}</h2><p v-if="error !== undefined" role="alert">{{ describeBridgeError(error) }}</p></section>
</template>
