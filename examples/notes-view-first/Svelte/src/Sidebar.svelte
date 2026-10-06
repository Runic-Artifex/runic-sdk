<script lang="ts">
  import type { SidebarPageReference } from "../../Frontend/src/generated/sidebar.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";

  let { page }: { page: SidebarPageReference } = $props();
  const sidebar = useView(() => page);
  const open = useCommand((name: "openHome" | "openNotes") => sidebar.client?.[name]());
</script>

<h2>Workspace</h2>
{#if sidebar.state && sidebar.client}
  <nav aria-label="Workspace navigation">
    <button data-go="home" aria-current={sidebar.state.selected === "Home" ? "page" : "false"}
      disabled={!sidebar.state.canOpenHome} onclick={() => open.run("openHome")}>Home</button>
    <button data-go="notes" aria-current={sidebar.state.selected === "Notes" ? "page" : "false"}
      disabled={!sidebar.state.canOpenNotes} onclick={() => open.run("openNotes")}>Notes</button>
  </nav>
{:else}
  <p>Connecting…</p>
{/if}
{#if open.error ?? sidebar.error}<p role="alert">{String(open.error ?? sidebar.error)}</p>{/if}
