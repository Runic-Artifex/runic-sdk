<svelte:options runes={true} />

<script lang="ts">
  import { useBridgeConnection, useCommand, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/svelte";
  import { describeBridgeError } from "@runic-artifex/views";
  import { connectWorkspace, type WorkspaceState } from "../generated/workspace.js";
  import CounterPage from "../lib/pages/CounterPage.svelte";
  import WelcomePage from "../lib/pages/WelcomePage.svelte";

  const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
  // While the Bridge reconnects, the clients keep the last state the Window sent.
  const statusText: Record<BridgeConnectionState, string> = {
    connecting: "Connecting to the .NET Window…",
    connected: "Connected to the .NET Window.",
    reconnecting: "Showing the last state the .NET Window sent.",
    lost: "Showing the last state the .NET Window sent.",
    // The application closed the Window; a connection drop that follows is not an error.
    closed: "The application closed this window.",
  };
  const workspace = useView(() => ({ connect: connectWorkspace }));
  const connection = useBridgeConnection();
  const navigate = useCommand((name: "showWelcome" | "showCounter") => workspace.client?.[name]());
  const error = $derived(navigate.error ?? workspace.error);
</script>

<main>
  <header><h1>Runic Views</h1><p>Window/View starter · SvelteKit</p></header>
  {#if connection.state === "reconnecting"}<p role="status">Reconnecting to the .NET Window…</p>{/if}
  {#if connection.state === "lost"}<p role="alert">The connection to the .NET Window was lost. The page reconnects when the application is back.</p>{/if}
  <nav aria-label="Main navigation">
    <button disabled={!workspace.client} onclick={() => navigate.run("showWelcome")}>Welcome</button>
    <button disabled={!workspace.client} onclick={() => navigate.run("showCounter")}>Counter</button>
  </nav>
  <ViewOutlet content={workspace.state?.main} registry={pages}>
    {#snippet fallback()}<p>Connecting to the Window…</p>{/snippet}
  </ViewOutlet>
  <p role="status">{error !== undefined ? describeBridgeError(error) : statusText[connection.state]}</p>
</main>
