<script lang="ts">
  import type { SidebarPageReference } from "../../Frontend/src/generated/sidebar.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: SidebarPageReference } = $props();
  const sidebar = pageState(() => page);
  let error = $state<string | undefined>();
  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<h2>Workspace</h2>
{#if sidebar.state && sidebar.view}
  <nav aria-label="Workspace navigation">
    <button data-go="home" aria-current={sidebar.state.selected === "Home" ? "page" : "false"}
      disabled={!sidebar.state.canOpenHome} onclick={() => run(() => sidebar.view!.openHome())}>Home</button>
    <button data-go="notes" aria-current={sidebar.state.selected === "Notes" ? "page" : "false"}
      disabled={!sidebar.state.canOpenNotes} onclick={() => run(() => sidebar.view!.openNotes())}>Notes</button>
  </nav>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? sidebar.error}<p role="alert">{String(error ?? sidebar.error)}</p>{/if}
