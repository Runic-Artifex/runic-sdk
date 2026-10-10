<script lang="ts">
  import type { BridgeOperation } from "@runic-artifex/views";
  import { useOperation } from "../../src/views/index.js";
  let { start }: { start: () => PromiseLike<BridgeOperation<number>> } = $props();
  export const operation = useOperation(() => start());
</script>
