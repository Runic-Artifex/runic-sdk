<script lang="ts">
  import type { EditorPageReference, EditorView } from "../../Frontend/src/generated/editor.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = pageState(() => page);
  let error = $state<string | undefined>();

  async function run(command: (view: EditorView) => Promise<unknown>) {
    const view = editor.view;
    if (!view) return;
    try { await command(view); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

{#if editor.state}
  <h2>Full editor</h2>
  <label>Title<input data-title value={editor.state.title} onchange={event => run(view => view.setTitle((event.currentTarget as HTMLInputElement).value))}></label>
  <label>Body<textarea data-body value={editor.state.body} onchange={event => run(view => view.setBody((event.currentTarget as HTMLTextAreaElement).value))}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => run(view => view.save())}>Save</button>
  <p data-message role="status">{editor.state.savedMessage}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? editor.error}<p role="alert">{String(error ?? editor.error)}</p>{/if}
