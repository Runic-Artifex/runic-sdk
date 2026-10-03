<svelte:options runes={true} />

<script lang="ts">
  import { useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/svelte/views";
  import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
  import CounterPage from "./pages/CounterPage.svelte";
  import WelcomePage from "./pages/WelcomePage.svelte";

  const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
  const workspace = useView(() => ({ connect: connectWorkspace }));
  let error = $state<string | undefined>(undefined);

  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<main>
  <header><h1>Runic Views</h1><p>Window/View starter · Svelte</p></header>
  <nav aria-label="Main navigation">
    <button disabled={!workspace.client} onclick={() => run(() => workspace.client!.showWelcome())}>Welcome</button>
    <button disabled={!workspace.client} onclick={() => run(() => workspace.client!.showCounter())}>Counter</button>
  </nav>
  <ViewOutlet content={workspace.state?.main} registry={pages} />
  <p role="status">{error ?? (workspace.error ? String(workspace.error) : "Connected to the .NET Window.")}</p>
</main>
