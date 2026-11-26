<script setup lang="ts">
import { computed } from "vue";
import { useCommand, useView } from "@runic-artifex/vue";
import { describeBridgeError } from "@runic-artifex/views";
import { aboutHeading } from "runic-library-starter";
import type { AboutPageReference } from "runic-library-starter/generated";

// The RunicCoreLibrary library's View. Its client, AboutPageReference, and aboutHeading come from the
// library's frontend package, which the library's build generates and builds.
const props = defineProps<{ page: AboutPageReference }>();
const { state, client, error: connection } = useView(() => props.page);
const { run: toggle, pending: toggling, error: toggleError } = useCommand(() => client.value?.toggleDetails());
const error = computed(() => toggleError.value ?? connection.value);
</script>

<template>
  <aside class="about">
    <h2>{{ state ? aboutHeading(state) : "Connecting…" }}</h2>
    <p v-if="state?.expanded">Shared ViewModels and Views, with their TypeScript clients in the library's frontend package.</p>
    <button :disabled="!client || toggling" @click="toggle()">{{ state?.expanded ? "Hide details" : "Show details" }}</button>
    <p v-if="error !== undefined" role="alert">{{ describeBridgeError(error) }}</p>
  </aside>
</template>
