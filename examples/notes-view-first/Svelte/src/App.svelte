<script lang="ts">
  import { onMount } from "svelte";
  import { connectShell, type ShellView } from "../../Frontend/src/generated/shell.js";
  import Shell from "./Shell.svelte";

  let shell = $state.raw<ShellView | undefined>();
  let error = $state<string | undefined>();

  onMount(() => {
    let active = true;
    let connected: ShellView | undefined;
    void connectShell().then(view => {
      if (!active) { view.dispose(); return; }
      connected = view;
      shell = view;
    }).catch(cause => { if (active) error = String(cause); });
    return () => {
      active = false;
      connected?.dispose();
    };
  });
</script>

{#if shell}
  <Shell {shell} />
{:else}
  <p id="status" role="status">{error ?? "Connecting…"}</p>
{/if}
