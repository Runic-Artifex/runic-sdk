<script setup lang="ts">
import { ref } from "vue";
import { useView } from "@runic-artifex/vue";
import { connectWorkspace } from "./generated/workspace.js";
import CounterPage from "./pages/CounterPage.vue";
import WelcomePage from "./pages/WelcomePage.vue";

const { state, client, error: connection } = useView({ connect: connectWorkspace });
const error = ref<string>();

async function run(command: () => Promise<unknown>) {
  try { await command(); error.value = undefined; }
  catch (cause) { error.value = String(cause); }
}

function showWelcome() {
  const workspace = client.value;
  if (workspace) void run(() => workspace.showWelcome());
}

function showCounter() {
  const workspace = client.value;
  if (workspace) void run(() => workspace.showCounter());
}
</script>

<template>
  <main>
    <header><h1>Runic Views</h1><p>Window/View starter · Vue</p></header>
    <nav aria-label="Main navigation">
      <button :disabled="!client" @click="showWelcome">Welcome</button>
      <button :disabled="!client" @click="showCounter">Counter</button>
    </nav>
    <CounterPage v-if="state?.main.kind === 'counter'" :key="state.main.kind" :page="state.main" />
    <WelcomePage v-else-if="state?.main.kind === 'welcome'" :key="state.main.kind" :page="state.main" />
    <p v-else>Connecting to the Window…</p>
    <p role="status">{{ error ?? (connection ? String(connection) : "Connected to the .NET Window.") }}</p>
  </main>
</template>
