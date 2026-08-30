<script lang="ts">
  import { createApplicationBridgeContext } from "../src/context.svelte.js";
  import type { SvelteApplicationBridge } from "../src/bridge.svelte.js";

  let { bridge }: {
    bridge: SvelteApplicationBridge<any, any, { _tag: "Changed"; snapshot: { count: number; revision: number } }, { count: number; revision: number }>;
  } = $props();

  const context = createApplicationBridgeContext<any, any, { _tag: "Changed"; snapshot: { count: number; revision: number } }, { count: number; revision: number }>();
  // The provider intentionally captures the mount-time bridge projection.
  // svelte-ignore state_referenced_locally
  context.provide(bridge);
</script>

<output>{bridge.snapshot?.count ?? 0}</output>
<span>{bridge.status}</span>
