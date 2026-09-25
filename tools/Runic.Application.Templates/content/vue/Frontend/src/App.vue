<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import { connectWorkspace, type WorkspaceState, type WorkspaceView } from "./generated/workspace.js";
import CounterPage from "./pages/CounterPage.vue";
import WelcomePage from "./pages/WelcomePage.vue";

const workspace = ref<WorkspaceView>();
const state = ref<WorkspaceState>();
const error = ref<string>();
let unsubscribe = () => {};
let active = true;

onMounted(() => {
  void connectWorkspace().then(client => {
    if (!active) { client.dispose(); return; }
    workspace.value = client;
    unsubscribe = client.subscribe(next => { state.value = next; });
  }).catch(cause => { error.value = String(cause); });
});

onUnmounted(() => {
  active = false;
  unsubscribe();
  workspace.value?.dispose();
});

async function run(command: () => Promise<unknown>) {
  try { await command(); error.value = undefined; }
  catch (cause) { error.value = String(cause); }
}

function showWelcome() {
  const client = workspace.value;
  if (client) void run(() => client.showWelcome());
}

function showCounter() {
  const client = workspace.value;
  if (client) void run(() => client.showCounter());
}
</script>

<template>
  <main>
    <header><h1>Runic Views</h1><p>Window/View starter · Vue</p></header>
    <nav aria-label="Main navigation">
      <button @click="showWelcome">Welcome</button>
      <button @click="showCounter">Counter</button>
    </nav>
    <CounterPage v-if="state?.main.kind === 'counter'" :key="state.main.kind" :page="state.main" />
    <WelcomePage v-else-if="state?.main.kind === 'welcome'" :key="state.main.kind" :page="state.main" />
    <p v-else>Connecting to the Window…</p>
    <p role="status">{{ error ?? "Connected to the .NET Window." }}</p>
  </main>
</template>
