<script lang="ts">
  import type { DocumentPageReference, DocumentState } from "../../Frontend/src/generated/document.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import Editor from "./Editor.svelte";
  import MirrorEditor from "./MirrorEditor.svelte";
  import Compact from "./Compact.svelte";
  import Preview from "./Preview.svelte";
  import ViewOutlet from "../../../../packages/web/svelte/src/views/ViewOutlet.svelte";
  import type { ViewRegistry } from "../../../../packages/web/svelte/src/views/view-registry.js";

  const paneViews = { editor: Editor, preview: Preview } satisfies ViewRegistry<DocumentState["currentPane"]>;
  const mirrorViews = { ...paneViews, editor: MirrorEditor } satisfies ViewRegistry<DocumentState["currentPane"]>;
  const compactViews = { editorCompact: Compact } satisfies ViewRegistry<DocumentState["compactNote"]>;
  let { page }: { page: DocumentPageReference } = $props();
  const document = useView(() => page);
  const show = useCommand((name: "showEditor" | "showPreview") => document.client?.[name]());
</script>

{#if document.state && document.client}
  <h1>Document</h1>
  <p class="muted">The nested route swaps Editor and Preview. The compact View stays mounted.</p>
  <div class="document">
    <div>
      <div class="tabs">
        <button data-pane="editor" aria-current={document.state.activePane === "Editor" ? "page" : "false"} onclick={() => show.run("showEditor")}>Editor</button>
        <button data-pane="preview" aria-current={document.state.activePane === "Preview" ? "page" : "false"} onclick={() => show.run("showPreview")}>Preview</button>
      </div>
      <section id="document-pane" class="card"><ViewOutlet content={document.state.currentPane} registry={paneViews} /></section>
    </div>
    {#if document.state.currentPane.kind === "editor"}
      <aside id="same-reference-editor" class="card"><ViewOutlet content={document.state.currentPane} registry={mirrorViews} /></aside>
    {/if}
    <aside id="compact-pane" class="card"><ViewOutlet content={document.state.compactNote} registry={compactViews} /></aside>
  </div>
{:else}
  <p>Connecting…</p>
{/if}
{#if show.error ?? document.error}<p role="alert">{String(show.error ?? document.error)}</p>{/if}
