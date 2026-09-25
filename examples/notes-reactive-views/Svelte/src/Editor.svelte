<script lang="ts">
  import type { EditorPageReference, EditorView } from "../../Frontend/src/generated/editor.js";
  import { EditorWrites } from "../../Frontend/src/editor-writes.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = pageState(() => page);
  let error = $state<string | undefined>();
  const writes = new EditorWrites(cause => { error = cause === undefined ? undefined : String(cause); });

  async function run(command: (view: EditorView) => Promise<unknown>) {
    const view = editor.view;
    if (!view) return;
    try { await command(view); error = undefined; }
    catch (cause) { error = String(cause); }
  }
  function write(command: (view: EditorView) => Promise<unknown>) {
    const view = editor.view;
    if (view) writes.enqueue(() => command(view));
  }
</script>

{#if editor.state}
  <h2>Full editor</h2>
  <label>Title<input data-title value={editor.state.title} onchange={event => { const value = event.currentTarget.value; write(view => view.setTitle(value)); }}></label>
  <label>Body<textarea data-body value={editor.state.body} onchange={event => { const value = event.currentTarget.value; write(view => view.setBody(value)); }}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => run(view => writes.run(() => view.save()))}>Save</button>
  <p data-message role="status">{editor.state.savedMessage}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? editor.error}<p role="alert">{String(error ?? editor.error)}</p>{/if}
