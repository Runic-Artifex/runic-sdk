<script lang="ts">
  import type { DocumentPageReference, DocumentState } from "../../Frontend/src/generated/document.js";
  import { pageState } from "./bridge-state.js";
  import Editor from "./Editor.svelte";
  import Compact from "./Compact.svelte";
  import Preview from "./Preview.svelte";
  import ViewOutlet from "../../../../packages/web/views-svelte/src/ViewOutlet.svelte";
  import type { ViewRegistry } from "../../../../packages/web/views-svelte/src/view-registry.js";

  const paneViews = { editor: Editor, preview: Preview } satisfies ViewRegistry<DocumentState["currentPane"]>;
  const compactViews = { editorCompact: Compact } satisfies ViewRegistry<DocumentState["compactNote"]>;
  let { page }: { page: DocumentPageReference } = $props();
  const document = pageState(() => page);
  let error = $state<string | undefined>();

  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

{#if document.state && document.view}
  <h1>Document</h1>
  <p class="muted">The nested route swaps Editor and Preview. The compact View stays mounted.</p>
  <div class="document">
    <div>
      <div class="tabs">
        <button data-pane="editor" aria-current={document.state.activePane === "Editor" ? "page" : "false"} onclick={() => run(() => document.view!.showEditor())}>Editor</button>
        <button data-pane="preview" aria-current={document.state.activePane === "Preview" ? "page" : "false"} onclick={() => run(() => document.view!.showPreview())}>Preview</button>
      </div>
      <section id="document-pane" class="card"><ViewOutlet content={document.state.currentPane} registry={paneViews} /></section>
    </div>
    {#if document.state.currentPane.kind === "editor"}
      <aside id="same-reference-editor" class="card"><ViewOutlet content={document.state.currentPane} registry={paneViews} /></aside>
    {/if}
    <aside id="compact-pane" class="card"><ViewOutlet content={document.state.compactNote} registry={compactViews} /></aside>
  </div>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? document.error}<p role="alert">{String(error ?? document.error)}</p>{/if}
