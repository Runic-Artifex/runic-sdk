<script lang="ts">
  import { onMount } from "svelte";
  import { useLatestOperation, useOperation, type LatestOperationHandle, type OperationHandle } from "@runic-artifex/svelte";
  import type { OperationsClient } from "./generated/operations";

  let { client, ready } = $props<{
    client: OperationsClient;
    ready: (handles: { operation: OperationHandle<[], string>; latest: LatestOperationHandle<string> }) => void;
  }>();
  const operation = useOperation(() => client.startLong());
  const latest = useLatestOperation<string>();
  onMount(() => { ready({ operation, latest }); });
</script>

<p>Operation: <output id="pending">{operation.pending ? "pending" : "idle"}</output></p>
<p>Cancellation: <output id="cancellation">{operation.cancellationRequested ? "requested" : "none"}</output></p>
<p>Terminal: <output id="terminal">{operation.cancelled ? "cancelled" : operation.error === undefined ? "none" : "error"}</output></p>
<p>Selection: <output id="selection">{latest.blocked ? "blocked" : latest.outcome?.ok ? latest.outcome.value : latest.pending ? "pending" : "none"}</output></p>
