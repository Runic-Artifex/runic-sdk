<script lang="ts">
  import { onDestroy } from "svelte";
  import type { EditorPageReference, EditorClient, EditorState } from "../../Frontend/src/generated/editor.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import { bridgeForm } from "./bridge-form.js";
  import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = useView(() => page);
  let error = $state<string | undefined>();
  const form = bridgeForm<EditorClient, EditorState>(editor, cause => { error = cause === undefined ? undefined : String(cause); });
  // Save waits for the form's queued writes. save.failure is its declared
  // failure; the latest run wins, and the button is disabled while it runs.
  const save = useCommand(async () => {
    let outcome: Awaited<ReturnType<EditorClient["save"]>> | undefined;
    await form.run(async view => { outcome = await view.save(); });
    return outcome;
  });
  const title = form.field("title", (view, value) => view.setTitle(value));
  const body = form.field("body", (view, value) => view.setBody(value));
  onDestroy(() => form.dispose());
</script>

{#if editor.state && editor.client}
  <h2>Editor</h2>
  <label>Title <input bind:value={title.get, title.set}></label>
  <label>Body <textarea bind:value={body.get, body.set}></textarea></label>
  <button data-save disabled={!editor.state.canSave || save.pending} onclick={() => save.run()}>Save</button>
  <p data-message role="status">{editor.state.isDirty ? "Unsaved changes. " : ""}{editor.state.savedMessage}</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if save.failure}<p role="alert">{describeSaveFailure(save.failure)}</p>
{:else if error ?? editor.error}<p role="alert">{String(error ?? editor.error)}</p>{/if}
