<script lang="ts">
  import type { EditorPageReference, EditorClient } from "../../Frontend/src/generated/editor.js";
  import { EditorWrites } from "../../Frontend/src/editor-writes.js";
  import { describeSaveFailure } from "../../Frontend/src/save-failure.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";

  let { page, handleInteractions = true }: { page: EditorPageReference; handleInteractions?: boolean } = $props();
  const editor = useView(() => page);
  let writeError = $state<unknown>();
  const writes = new EditorWrites(cause => { writeError = cause; });
  const connectedEditor = $derived(editor.client);

  $effect(() => {
    if (!connectedEditor || !handleInteractions) return;
    return connectedEditor.interactions.confirmDiscard.handle(async (request, { signal }) => {
      if (signal.aborted) throw signal.reason;
      return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
    });
  });

  const command = useCommand((name: "save" | "discard") => {
    const view = editor.client;
    if (!view) return undefined;
    return name === "save" ? writes.run(() => view.save()) : writes.run(() => view.discard());
  });
  function write(command: (view: EditorClient) => Promise<unknown>) {
    const view = editor.client;
    if (view) writes.enqueue(() => command(view));
  }
</script>

{#if editor.state}
  <h2>Full editor</h2>
  <label>Title<input data-title value={editor.state.title} onchange={event => { const value = event.currentTarget.value; write(view => view.setTitle(value)); }}></label>
  <label>Body<textarea data-body value={editor.state.body} onchange={event => { const value = event.currentTarget.value; write(view => view.setBody(value)); }}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => command.run("save")}>Save</button>
  <button data-discard disabled={!editor.state.canDiscard} onclick={() => command.run("discard")}>Discard changes</button>
  <p data-message role="status">{editor.state.savedMessage}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if command.failure}<p role="alert">{describeSaveFailure(command.failure)}</p>
{:else if command.error ?? writeError ?? editor.error}<p role="alert">{String(command.error ?? writeError ?? editor.error)}</p>{/if}
