<script setup lang="ts">
import { useCommand, useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/vue";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import CounterPage from "./pages/CounterPage.vue";
import WelcomePage from "./pages/WelcomePage.vue";

const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
const { state, client, error: connection } = useView({ connect: connectWorkspace });
const navigate = useCommand((name: "showWelcome" | "showCounter") => client.value?.[name]());
</script>

<template>
  <main>
    <header><h1>Runic Views</h1><p>Window/View starter · Vue</p></header>
    <nav aria-label="Main navigation">
      <button :disabled="!client" @click="navigate.run('showWelcome')">Welcome</button>
      <button :disabled="!client" @click="navigate.run('showCounter')">Counter</button>
    </nav>
    <ViewOutlet :content="state?.main" :registry="pages"><p>Connecting to the Window…</p></ViewOutlet>
    <p role="status">{{ String(navigate.error ?? connection ?? "Connected to the .NET Window.") }}</p>
  </main>
</template>
