<script setup lang="ts">
import { computed } from "vue";
import { useBridgeConnection, useCommand, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/vue";
import { describeBridgeError } from "@runic-artifex/views";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import CounterPage from "./pages/CounterPage.vue";
import WelcomePage from "./pages/WelcomePage.vue";

const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, string> = {
  connecting: "Connecting to the .NET Window…",
  connected: "Connected to the .NET Window.",
  reconnecting: "Showing the last state the .NET Window sent.",
  lost: "Showing the last state the .NET Window sent.",
};
const { state, client, error: connectError } = useView({ connect: connectWorkspace });
const { state: connection } = useBridgeConnection();
const { run: navigate, error: navigationError } = useCommand((name: "showWelcome" | "showCounter") => client.value?.[name]());
const status = computed(() => {
  const error = navigationError.value ?? connectError.value;
  return error !== undefined ? describeBridgeError(error) : statusText[connection.value];
});
</script>

<template>
  <main>
    <header><h1>Runic Views</h1><p>Window/View starter · Vue</p></header>
    <p v-if="connection === 'reconnecting'" role="status">Reconnecting to the .NET Window…</p>
    <p v-if="connection === 'lost'" role="alert">The connection to the .NET Window was lost. The page reconnects when the application is back.</p>
    <nav aria-label="Main navigation">
      <button :disabled="!client" @click="navigate('showWelcome')">Welcome</button>
      <button :disabled="!client" @click="navigate('showCounter')">Counter</button>
    </nav>
    <ViewOutlet :content="state?.main" :registry="pages"><p>Connecting to the Window…</p></ViewOutlet>
    <p role="status">{{ status }}</p>
  </main>
</template>
