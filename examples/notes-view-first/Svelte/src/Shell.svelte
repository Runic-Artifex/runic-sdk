<script lang="ts">
  import type { ShellClient } from "../../Frontend/src/generated/shell.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import Sidebar from "./Sidebar.svelte";
  import Home from "./Home.svelte";
  import Document from "./Document.svelte";
  import ConfirmNavigation from "./ConfirmNavigation.svelte";

  let { shell }: { shell: ShellClient } = $props();
  const source = useView(() => shell);
  let state = $derived(source.state!);
</script>

<header><strong>Composed Notes</strong><span>Plain web component · no ViewModel</span></header>
<div class="layout">
  <aside id="sidebar" aria-label="Workspace sidebar">
    {#key state.sidebar}<Sidebar page={state.sidebar} />{/key}
  </aside>
  <main id="main">
    {#key state.main}
      {#if state.main?.kind === "home"}
        <Home page={state.main} />
      {:else if state.main}
        <Document page={state.main} />
      {/if}
    {/key}
  </main>
</div>
{#if state.dialog}
  {#key state.dialog}<ConfirmNavigation page={state.dialog} />{/key}
{/if}
<p id="status" class="status" role="status">Connected.</p>
