<script lang="ts">
  import type { DocumentPageReference, DocumentState } from "../../Frontend/src/generated/document.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import Editor from "./Editor.svelte";
  import MirrorEditor from "./MirrorEditor.svelte";
  import Compact from "./Compact.svelte";
  import Preview from "./Preview.svelte";
  import { describeSaveFailure } from "../../Frontend/src/save-failure.js";
  import ViewOutlet from "../../../../packages/web/svelte/src/views/ViewOutlet.svelte";
  import type { ViewRegistry } from "../../../../packages/web/svelte/src/views/view-registry.js";

  const paneViews = { editor: Editor, preview: Preview } satisfies ViewRegistry<DocumentState["currentPane"]>;
  const mirrorViews = { ...paneViews, editor: MirrorEditor } satisfies ViewRegistry<DocumentState["currentPane"]>;
  const compactViews = { editorCompact: Compact } satisfies ViewRegistry<DocumentState["compactNote"]>;
  let { page }: { page: DocumentPageReference } = $props();
  const document = useView(() => page);
  const show = useCommand((name: "showEditor" | "showPreview") => document.client?.[name]());
  const close = useCommand(() => document.client?.close());
  const saveAndClose = useCommand(() => document.client?.saveAndClose());
  const connectedDocument = $derived(document.client);

  // The leave guard asks here when this View's Close or Save and close leaves
  // unsaved edits. Answering false keeps the document and its edits.
  $effect(() => {
    if (!connectedDocument) return;
    return connectedDocument.interactions.confirmLeave.handle(async (request, { signal }) => {
      if (signal.aborted) throw signal.reason;
      return window.confirm(`Close “${request.title}” and lose its unsaved changes?`);
    });
  });
</script>

{#if document.state && document.client}
  <h1>Document</h1>
  <p class="muted">A child region swaps Editor and Preview. The compact View stays mounted.</p>
  <div class="document">
    <div>
      <div class="tabs">
        <button data-pane="editor" aria-current={document.state.activePane === "Editor" ? "page" : "false"} onclick={() => show.run("showEditor")}>Editor</button>
        <button data-pane="preview" aria-current={document.state.activePane === "Preview" ? "page" : "false"} onclick={() => show.run("showPreview")}>Preview</button>
      </div>
      <section id="document-pane" class="card"><ViewOutlet content={document.state.currentPane} registry={paneViews} /></section>
      <div class="tabs">
        <button data-save-close onclick={() => saveAndClose.run()}>Save and close</button>
        <button data-close onclick={() => close.run()}>Close</button>
      </div>
      <p data-document-status class="muted" role="status">{document.state.status}</p>
      {#if saveAndClose.failure}<p data-close-failure role="alert">{describeSaveFailure(saveAndClose.failure)}</p>{/if}
    </div>
    {#if document.state.currentPane?.kind === "editor"}
      <aside id="same-reference-editor" class="card"><ViewOutlet content={document.state.currentPane} registry={mirrorViews} /></aside>
    {/if}
    <aside id="compact-pane" class="card"><ViewOutlet content={document.state.compactNote} registry={compactViews} /></aside>
  </div>
{:else}
  <p>Connecting…</p>
{/if}
{#if show.error ?? close.error ?? saveAndClose.error ?? document.error}<p role="alert">{String(show.error ?? close.error ?? saveAndClose.error ?? document.error)}</p>{/if}
