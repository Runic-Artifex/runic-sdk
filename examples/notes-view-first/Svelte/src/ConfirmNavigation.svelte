<script lang="ts">
  import { onMount } from "svelte";
  import type { ConfirmNavigationPageReference } from "../../Frontend/src/generated/confirmNavigation.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: ConfirmNavigationPageReference } = $props();
  const dialog = pageState(() => page);
  let error = $state<string | undefined>();
  let cancelButton: HTMLButtonElement;
  let confirmButton: HTMLButtonElement;
  let focused = false;

  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
  function keydown(event: KeyboardEvent) {
    if (event.key === "Escape") {
      event.preventDefault();
      if (dialog.view) void run(() => dialog.view!.cancel());
    }
    if (event.key === "Tab") {
      if (event.shiftKey && document.activeElement === cancelButton) {
        event.preventDefault(); confirmButton.focus();
      } else if (!event.shiftKey && document.activeElement === confirmButton) {
        event.preventDefault(); cancelButton.focus();
      }
    }
  }
  onMount(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    return () => previousFocus?.focus();
  });
  $effect(() => {
    if (dialog.state?.canCancel && cancelButton && !focused) {
      cancelButton.focus();
      focused = true;
    }
  });
</script>

<div id="modal" class="modal" role="presentation" onkeydown={keydown}>
  <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title" tabindex="-1">
    <h2 id="dialog-title">Unsaved changes</h2>
    <p data-message>{dialog.state?.message ?? "Connecting…"}</p>
    <div class="dialog-actions">
      <button data-cancel bind:this={cancelButton} disabled={!dialog.state?.canCancel}
        onclick={() => dialog.view && run(() => dialog.view!.cancel())}>Keep editing</button>
      <button data-confirm bind:this={confirmButton} disabled={!dialog.state?.canConfirm}
        onclick={() => dialog.view && run(() => dialog.view!.confirm())}>Discard changes</button>
    </div>
    {#if error ?? dialog.error}<p role="alert">{String(error ?? dialog.error)}</p>{/if}
  </div>
</div>
