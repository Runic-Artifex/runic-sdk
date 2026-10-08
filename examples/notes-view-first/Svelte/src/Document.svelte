<script lang="ts">
  import type { DocumentPageReference } from "../../Frontend/src/generated/document.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import Editor from "./Editor.svelte";
  import Preview from "./Preview.svelte";

  let { page }: { page: DocumentPageReference } = $props();
  const document = useView(() => page);
  const show = useCommand((name: "showEditor" | "showPreview") => document.client?.[name]());
</script>

{#if document.state && document.client}
  <h1>Document</h1>
  <p class="muted">This area has its own ViewModel and a nested Editor/Preview outlet.</p>
  <div class="tabs">
    <button data-pane="editor" aria-current={document.state.activePane === "Editor" ? "page" : "false"}
      disabled={!document.state.canShowEditor} onclick={() => show.run("showEditor")}>Editor</button>
    <button data-pane="preview" aria-current={document.state.activePane === "Preview" ? "page" : "false"}
      disabled={!document.state.canShowPreview} onclick={() => show.run("showPreview")}>Preview</button>
  </div>
  <section id="document-pane" class="card">
    {#key document.state.currentPane}
      {#if document.state.currentPane?.kind === "editor"}
        <Editor page={document.state.currentPane} />
      {:else if document.state.currentPane}
        <Preview page={document.state.currentPane} />
      {/if}
    {/key}
  </section>
{:else}
  <p>Connecting…</p>
{/if}
{#if show.error ?? document.error}<p role="alert">{String(show.error ?? document.error)}</p>{/if}
