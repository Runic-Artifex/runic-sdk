<script lang="ts">
  import { connectShell } from "../../Frontend/src/generated/shell.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import MenuBar from "./MenuBar.svelte";
  import Shell from "./Shell.svelte";

  const shell = useView(() => ({ connect: connectShell }));
</script>

<MenuBar />
{#if shell.client}
  <Shell shell={shell.client} />
{:else}
  <p id="status" role="status">{shell.error === undefined ? "Connecting…" : String(shell.error)}</p>
{/if}
