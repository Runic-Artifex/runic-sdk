<svelte:options runes={true} />

<script lang="ts">
  import { onMount } from "svelte";
  import { ViewOutlet, type ViewRegistry } from "@runic-artifex/svelte/views";
  import { connectWorkspace, type WorkspaceState, type WorkspaceView } from "./generated/workspace.js";
  import CounterPage from "./pages/CounterPage.svelte";
  import WelcomePage from "./pages/WelcomePage.svelte";

  const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
  let workspace = $state.raw<WorkspaceView | undefined>(undefined);
  let workspaceState = $state.raw<WorkspaceState | undefined>(undefined);
  let error = $state<string | undefined>(undefined);
  let unsubscribe = () => {};

  onMount(() => {
    let active = true;
    void connectWorkspace().then(client => {
      if (!active) { client.dispose(); return; }
      workspace = client;
      unsubscribe = client.subscribe(next => { workspaceState = next; });
    }).catch(cause => { error = String(cause); });
    return () => { active = false; unsubscribe(); workspace?.dispose(); };
  });

  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<main>
  <header><h1>Runic Views</h1><p>Window/View starter · Svelte</p></header>
  <nav aria-label="Main navigation">
    <button onclick={() => workspace && run(() => workspace!.showWelcome())}>Welcome</button>
    <button onclick={() => workspace && run(() => workspace!.showCounter())}>Counter</button>
  </nav>
  <ViewOutlet content={workspaceState?.main} registry={pages} />
  <p role="status">{error ?? "Connected to the .NET Window."}</p>
</main>
