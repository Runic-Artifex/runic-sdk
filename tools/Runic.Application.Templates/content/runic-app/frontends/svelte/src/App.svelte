<svelte:options runes={true} />

<script lang="ts">
  import { useCommand, useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/svelte/views";
  import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
  import CounterPage from "./pages/CounterPage.svelte";
  import WelcomePage from "./pages/WelcomePage.svelte";

  const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
  const workspace = useView(() => ({ connect: connectWorkspace }));
  const navigate = useCommand((name: "showWelcome" | "showCounter") => workspace.client?.[name]());
</script>

<main>
  <header><h1>Runic Views</h1><p>Window/View starter · Svelte</p></header>
  <nav aria-label="Main navigation">
    <button disabled={!workspace.client} onclick={() => navigate.run("showWelcome")}>Welcome</button>
    <button disabled={!workspace.client} onclick={() => navigate.run("showCounter")}>Counter</button>
  </nav>
  <ViewOutlet content={workspace.state?.main} registry={pages}>
    {#snippet fallback()}<p>Connecting to the Window…</p>{/snippet}
  </ViewOutlet>
  <p role="status">{String(navigate.error ?? workspace.error ?? "Connected to the .NET Window.")}</p>
</main>
