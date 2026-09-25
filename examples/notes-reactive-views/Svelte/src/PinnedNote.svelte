<script lang="ts">
  import type { PinnedNotePageReference } from "../../Frontend/src/generated/pinnedNote.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: PinnedNotePageReference } = $props();
  const note = pageState(() => page);
</script>

{#if note.state}<span>{note.state.label}</span>{:else}<span>Connecting…</span>{/if}
{#if note.error}<span role="alert">{String(note.error)}</span>{/if}
