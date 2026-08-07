<script lang="ts">
  import type { SvelteApplicationBridge } from "../src/index.js";

  type Snapshot = { readonly count: number };
  let { bridge }: {
    bridge: SvelteApplicationBridge<unknown, unknown, unknown, Snapshot>;
  } = $props();
</script>

<output>{bridge.snapshot?.count ?? 0}</output>
<span>{bridge.status}</span>

