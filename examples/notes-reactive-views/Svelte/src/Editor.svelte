<script lang="ts">
  import type { EditorPageReference, EditorClient } from "../../Frontend/src/generated/editor.js";
  import { EditorWrites } from "../../Frontend/src/editor-writes.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";

  let { page, handleInteractions = true }: { page: EditorPageReference; handleInteractions?: boolean } = $props();
  const editor = useView(() => page);
  let error = $state<string | undefined>();
  const writes = new EditorWrites(cause => { error = cause === undefined ? undefined : String(cause); });
  const connectedEditor = $derived(editor.client);

  $effect(() => {
    if (!connectedEditor || !handleInteractions) return;
    return connectedEditor.interactions.confirmDiscard.handle(async (request, { signal }) => {
      if (signal.aborted) throw signal.reason;
      return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
    });
  });

  async function run(command: (view: EditorClient) => Promise<unknown>) {
    const view = editor.client;
    if (!view) return;
    try { await command(view); error = undefined; }
    catch (cause) { error = String(cause); }
  }
  function write(command: (view: EditorClient) => Promise<unknown>) {
    const view = editor.client;
    if (view) writes.enqueue(() => command(view));
  }
</script>

{#if editor.state}
  <h2>Full editor</h2>
  <label>Title<input data-title value={editor.state.title} onchange={event => { const value = event.currentTarget.value; write(view => view.setTitle(value)); }}></label>
  <label>Body<textarea data-body value={editor.state.body} onchange={event => { const value = event.currentTarget.value; write(view => view.setBody(value)); }}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => run(view => writes.run(() => view.save()))}>Save</button>
  <button data-discard disabled={!editor.state.canDiscard} onclick={() => run(view => writes.run(() => view.discard()))}>Discard changes</button>
  <p data-message role="status">{editor.state.savedMessage}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? editor.error}<p role="alert">{String(error ?? editor.error)}</p>{/if}
