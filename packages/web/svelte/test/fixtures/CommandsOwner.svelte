<script lang="ts">
  import { useCommands } from "../../src/views/index.js";
  import type { OrdersClient } from "../commands.js";
  let { client }: { client: OrdersClient | undefined } = $props();
  export const commands = useCommands(() => ({ client }));
</script>

<output>{commands.refresh.pending}:{(commands.refresh.error as Error | undefined)?.message ?? ""}:{commands.archive.pending}</output>
