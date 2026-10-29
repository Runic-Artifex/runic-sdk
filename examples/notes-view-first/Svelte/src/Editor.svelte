<script lang="ts">
  import { EditorFields, type EditorPageReference } from "../../Frontend/src/generated/editor.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import { useField } from "../../../../packages/web/svelte/src/views/use-field.svelte.js";
  import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = useView(() => page);
  // A field is written after a pause in typing or when it loses focus, not on every keystroke.
  const title = useField(() => editor, EditorFields.title, { commit: "pause" });
  const body = useField(() => editor, EditorFields.body, { commit: "pause" });
  // Save waits for the fields' writes, and does not start after one that failed.
  // save.failure is its declared failure; the latest run wins, and the button
  // is disabled while it runs.
  const save = useCommand(async () => {
    await Promise.all([title.commit(), body.commit()]);
    if (title.error !== undefined || body.error !== undefined) return undefined;
    return editor.client?.save();
  });
</script>

{#if editor.state && editor.client}
  <h2>Editor</h2>
  <label>Title <input {@attach title.attach}></label>
  <label>Body <textarea {@attach body.attach}></textarea></label>
  <button data-save disabled={!editor.state.canSave || save.pending} onclick={() => save.run()}>Save</button>
  <p data-message role="status">{editor.state.isDirty ? "Unsaved changes. " : ""}{editor.state.savedMessage}</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if save.failure}<p role="alert">{describeSaveFailure(save.failure)}</p>
{:else if save.error ?? title.error ?? body.error ?? editor.error}<p role="alert">{String(save.error ?? title.error ?? body.error ?? editor.error)}</p>{/if}
