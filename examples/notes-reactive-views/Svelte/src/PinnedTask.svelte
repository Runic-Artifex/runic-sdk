<script lang="ts">
  import type { PinnedTaskPageReference } from "../../Frontend/src/generated/pinnedTask.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: PinnedTaskPageReference } = $props();
  const task = pageState(() => page);
</script>

{#if task.state}<span>Task: {task.state.priority}</span>{:else}<span>Connecting…</span>{/if}
{#if task.error}<span role="alert">{String(task.error)}</span>{/if}
