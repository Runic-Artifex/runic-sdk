<script lang="ts">
  import { onMount } from "svelte";
  import { useOperation, type OperationHandle } from "@runic-artifex/svelte/views";
  import type { OperationsClient } from "./generated/operations";

  let { client, ready } = $props<{
    client: OperationsClient;
    ready: (operation: OperationHandle<[], string>) => void;
  }>();
  const operation = useOperation(() => client.startLong());
  onMount(() => { ready(operation); });
</script>

<p>Operation: <output id="pending">{operation.pending ? "pending" : "idle"}</output></p>
<p>Cancellation: <output id="cancellation">{operation.cancellationRequested ? "requested" : "none"}</output></p>
