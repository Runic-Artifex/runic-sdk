<script lang="ts">
  import type { ShellView } from "../../Frontend/src/generated/shell.js";
  import { bridgeState } from "./bridge-state.js";
  import Sidebar from "./Sidebar.svelte";
  import Home from "./Home.svelte";
  import Document from "./Document.svelte";
  import ConfirmNavigation from "./ConfirmNavigation.svelte";

  let { shell }: { shell: ShellView } = $props();
  let source = $derived(bridgeState(shell));
  let state = $derived(source.current);
</script>

<header><strong>Composed Notes</strong><span>Plain web component · no ViewModel</span></header>
<div class="layout">
  <aside id="sidebar" aria-label="Workspace sidebar">
    {#key state.sidebar}<Sidebar page={state.sidebar} />{/key}
  </aside>
  <main id="main">
    {#key state.main}
      {#if state.main.kind === "home"}
        <Home page={state.main} />
      {:else}
        <Document page={state.main} />
      {/if}
    {/key}
  </main>
</div>
{#if state.dialog}
  {#key state.dialog}<ConfirmNavigation page={state.dialog} />{/key}
{/if}
<p id="status" class="status" role="status">Connected.</p>
