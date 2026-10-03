<script lang="ts">
  import type { PinnedNotePageReference } from "../../Frontend/src/generated/pinnedNote.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";

  let { page }: { page: PinnedNotePageReference } = $props();
  const note = useView(() => page);
</script>

{#if note.state}<span>{note.state.label}</span>{:else}<span>Connecting…</span>{/if}
{#if note.error}<span role="alert">{String(note.error)}</span>{/if}
