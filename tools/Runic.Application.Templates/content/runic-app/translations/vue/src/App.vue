<script setup lang="ts">
import { computed } from "vue";
import { useBridgeConnection, useCommand, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/vue";
import { describeBridgeError } from "@runic-artifex/views";
import { languages, m } from "./translations";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import CounterPage from "./pages/CounterPage.vue";
import WelcomePage from "./pages/WelcomePage.vue";

const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, () => string> = {
  connecting: m.status_connecting,
  connected: m.status_connected,
  reconnecting: m.status_stale,
  lost: m.status_stale,
  // The application closed the Window; a connection drop that follows is not an error.
  closed: m.status_closed,
};
const { state, client, error: connectError } = useView({ connect: connectWorkspace });
const { state: connection } = useBridgeConnection();
const { run: navigate, error: navigationError } = useCommand((name: "showWelcome" | "showCounter") => client.value?.[name]());
const { run: choose, error: languageError } = useCommand((language: string) => client.value?.chooseLanguage(language));
const status = computed(() => {
  const error = navigationError.value ?? languageError.value ?? connectError.value;
  return error !== undefined ? describeBridgeError(error) : statusText[connection.value]();
});
</script>

<template>
  <main>
    <header>
      <h1>{{ m.app_title() }}</h1><p>{{ m.app_subtitle({ frontend: "Vue" }) }}</p>
      <!-- The application owns the language: the page asks for one, and every window follows. -->
      <label>{{ m.app_language() }} <select :value="state?.language ?? ''" :disabled="!client"
        @change="choose(($event.target as HTMLSelectElement).value)">
        <option v-for="language in languages" :key="language.tag" :value="language.tag">{{ language.name }}</option>
      </select></label>
    </header>
    <p v-if="connection === 'reconnecting'" role="status">{{ m.status_reconnecting() }}</p>
    <p v-if="connection === 'lost'" role="alert">{{ m.status_lost() }}</p>
    <nav :aria-label="m.nav_label()">
      <button :disabled="!client" @click="navigate('showWelcome')">{{ m.nav_welcome() }}</button>
      <button :disabled="!client" @click="navigate('showCounter')">{{ m.nav_counter() }}</button>
    </nav>
    <ViewOutlet :content="state?.main" :registry="pages"><p>{{ m.app_loading() }}</p></ViewOutlet>
    <p role="status">{{ status }}</p>
  </main>
</template>
