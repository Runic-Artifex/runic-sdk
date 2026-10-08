<script lang="ts">
  import { onMount } from "svelte";
  import type { ConfirmNavigationPageReference } from "../../Frontend/src/generated/confirmNavigation.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import { focusOrigin, restoreFocus } from "../../Frontend/src/focus.js";

  let { page }: { page: ConfirmNavigationPageReference } = $props();
  const dialog = useView(() => page);
  const answer = useCommand((name: "cancel" | "confirm") => dialog.client?.[name]());
  let cancelButton: HTMLButtonElement;
  let confirmButton: HTMLButtonElement;
  let focused = false;

  function keydown(event: KeyboardEvent) {
    if (event.key === "Escape") {
      event.preventDefault();
      void answer.run("cancel");
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
    const previousFocus = focusOrigin();
    return () => restoreFocus(previousFocus);
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
        onclick={() => answer.run("cancel")}>Keep editing</button>
      <button data-confirm bind:this={confirmButton} disabled={!dialog.state?.canConfirm}
        onclick={() => answer.run("confirm")}>Discard changes</button>
    </div>
    {#if answer.error ?? dialog.error}<p role="alert">{String(answer.error ?? dialog.error)}</p>{/if}
  </div>
</div>
