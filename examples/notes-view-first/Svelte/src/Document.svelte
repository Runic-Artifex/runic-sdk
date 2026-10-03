<script lang="ts">
  import type { DocumentPageReference } from "../../Frontend/src/generated/document.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import Editor from "./Editor.svelte";
  import Preview from "./Preview.svelte";

  let { page }: { page: DocumentPageReference } = $props();
  const document = useView(() => page);
  let error = $state<string | undefined>();
  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

{#if document.state && document.client}
  <h1>Document</h1>
  <p class="muted">This area has its own ViewModel and a nested Editor/Preview outlet.</p>
  <div class="tabs">
    <button data-pane="editor" aria-current={document.state.activePane === "Editor" ? "page" : "false"}
      disabled={!document.state.canShowEditor} onclick={() => run(() => document.client!.showEditor())}>Editor</button>
    <button data-pane="preview" aria-current={document.state.activePane === "Preview" ? "page" : "false"}
      disabled={!document.state.canShowPreview} onclick={() => run(() => document.client!.showPreview())}>Preview</button>
  </div>
  <section id="document-pane" class="card">
    {#key document.state.currentPane}
      {#if document.state.currentPane.kind === "editor"}
        <Editor page={document.state.currentPane} />
      {:else}
        <Preview page={document.state.currentPane} />
      {/if}
    {/key}
  </section>
{:else}
  <p>Connecting…</p>
{/if}
{#if error ?? document.error}<p role="alert">{String(error ?? document.error)}</p>{/if}
