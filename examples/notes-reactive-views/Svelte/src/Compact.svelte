<script lang="ts">
  import type { EditorCompactPageReference } from "../../Frontend/src/generated/editor.js";
  import { pageState } from "./bridge-state.js";
  let { page }: { page: EditorCompactPageReference } = $props();
  const editor = pageState(() => page);
</script>

{#if editor.state}
  <h2>Compact View</h2><p class="muted">Contract: compact</p>
  <strong data-title>{editor.state.title}</strong><p data-body>{editor.state.body || "Nothing written yet."}</p>
  <p data-activation class="muted">Activated {editor.state.activationCount} × · deactivated {editor.state.deactivationCount} ×</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if editor.error}<p role="alert">{String(editor.error)}</p>{/if}
