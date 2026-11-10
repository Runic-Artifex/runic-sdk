<svelte:options runes={true} />

<script lang="ts">
  import { useBridgeConnection, useCommand, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/svelte";
  import { describeBridgeError } from "@runic-artifex/views";
  import { languages, m } from "./translations";
  import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
  import CounterPage from "./pages/CounterPage.svelte";
  import WelcomePage from "./pages/WelcomePage.svelte";

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
  const workspace = useView(() => ({ connect: connectWorkspace }));
  const connection = useBridgeConnection();
  const navigate = useCommand((name: "showWelcome" | "showCounter") => workspace.client?.[name]());
  const choose = useCommand((language: string) => workspace.client?.chooseLanguage(language));
  const error = $derived(navigate.error ?? choose.error ?? workspace.error);
</script>

<main>
  <header>
    <h1>{m.app_title()}</h1><p>{m.app_subtitle({ frontend: "Svelte" })}</p>
    <!-- The application owns the language: the page asks for one, and every window follows. -->
    <label>{m.app_language()} <select value={workspace.state?.language ?? ""} disabled={!workspace.client}
      onchange={event => choose.run(event.currentTarget.value)}>
      {#each languages as language (language.tag)}<option value={language.tag}>{language.name}</option>{/each}
    </select></label>
  </header>
  {#if connection.state === "reconnecting"}<p role="status">{m.status_reconnecting()}</p>{/if}
  {#if connection.state === "lost"}<p role="alert">{m.status_lost()}</p>{/if}
  <nav aria-label={m.nav_label()}>
    <button disabled={!workspace.client} onclick={() => navigate.run("showWelcome")}>{m.nav_welcome()}</button>
    <button disabled={!workspace.client} onclick={() => navigate.run("showCounter")}>{m.nav_counter()}</button>
  </nav>
  <ViewOutlet content={workspace.state?.main} registry={pages}>
    {#snippet fallback()}<p>{m.app_loading()}</p>{/snippet}
  </ViewOutlet>
  <p role="status">{error !== undefined ? describeBridgeError(error) : statusText[connection.state]()}</p>
</main>
