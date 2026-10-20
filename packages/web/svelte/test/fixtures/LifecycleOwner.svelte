<script lang="ts">
  import type { ViewSource } from "@runic-artifex/views";
  import { useCollectionViewport, useCommand, useView } from "../../src/views/index.js";
  let { source, command }: { source: ViewSource<never>; command: () => Promise<number> } = $props();
  export const view = useView(() => source);
  export const list = useCollectionViewport(() => ({ totalCount: 10, rowHeight: 20 }));
  export const run = useCommand(() => command());
</script>
