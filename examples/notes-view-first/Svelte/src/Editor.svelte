<script lang="ts">
  import { onDestroy } from "svelte";
  import type { EditorPageReference, EditorView, EditorState } from "../../Frontend/src/generated/editor.js";
  import { pageState } from "./bridge-state.js";
  import { bridgeForm } from "./bridge-form.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = pageState(() => page);
  let error = $state<string | undefined>();
  const form = bridgeForm<EditorView, EditorState>(editor, cause => { error = cause === undefined ? undefined : String(cause); });
  const title = form.field("title", (view, value) => view.setTitle(value));
  const body = form.field("body", (view, value) => view.setBody(value));
  onDestroy(() => form.dispose());
</script>

{#if editor.state && editor.view}
  <h2>Editor</h2>
  <label>Title <input bind:value={title.get, title.set}></label>
  <label>Body <textarea bind:value={body.get, body.set}></textarea></label>
  <button data-save disabled={!editor.state.canSave} onclick={() => form.run(view => view.save())}>Save</button>
  <p data-message role="status">{editor.state.isDirty ? "Unsaved changes. " : ""}{editor.state.savedMessage}</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? editor.error}<p role="alert">{String(error ?? editor.error)}</p>{/if}
