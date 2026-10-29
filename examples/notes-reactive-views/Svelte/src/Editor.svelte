<script lang="ts">
  import { EditorFields, type EditorPageReference } from "../../Frontend/src/generated/editor.js";
  import { describeSaveFailure } from "../../Frontend/src/save-failure.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import { useField } from "../../../../packages/web/svelte/src/views/use-field.svelte.js";

  let { page, handleInteractions = true }: { page: EditorPageReference; handleInteractions?: boolean } = $props();
  const editor = useView(() => page);
  // A field is written after a pause in typing or when it loses focus, not on every keystroke.
  const title = useField(() => editor, EditorFields.title, { commit: "pause" });
  const body = useField(() => editor, EditorFields.body, { commit: "pause" });
  const connectedEditor = $derived(editor.client);

  $effect(() => {
    if (!connectedEditor || !handleInteractions) return;
    return connectedEditor.interactions.confirmDiscard.handle(async (request, { signal }) => {
      if (signal.aborted) throw signal.reason;
      return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
    });
  });

  // Save and Discard follow the fields' writes, and do not start after one that failed.
  const command = useCommand(async (name: "save" | "discard") => {
    await Promise.all([title.commit(), body.commit()]);
    const view = editor.client;
    if (!view || title.error !== undefined || body.error !== undefined) return undefined;
    return name === "save" ? view.save() : view.discard();
  });
</script>

{#if editor.state}
  <h2>Full editor</h2>
  <label>Title<input data-title {@attach title.attach}></label>
  <label>Body<textarea data-body {@attach body.attach}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => command.run("save")}>Save</button>
  <button data-discard disabled={!editor.state.canDiscard} onclick={() => command.run("discard")}>Discard changes</button>
  <p data-message role="status">{editor.state.savedMessage}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if command.failure}<p role="alert">{describeSaveFailure(command.failure)}</p>
{:else if command.error ?? title.error ?? body.error ?? editor.error}<p role="alert">{String(command.error ?? title.error ?? body.error ?? editor.error)}</p>{/if}
