<script lang="ts">
  import { onMount } from "svelte";
  import { startRunicHostedBridgeAfterBootstrap, type RunicHostedBrowserBootstrap, type RunicHostedBrowserBridge } from "../src/hosted.svelte.js";

  let { bootstrap, bridge }: {
    bootstrap: RunicHostedBrowserBootstrap;
    bridge: RunicHostedBrowserBridge;
  } = $props();

  // The hydration guard deliberately captures the server bootstrap once.
  onMount(() => {
    if (!startRunicHostedBridgeAfterBootstrap(bootstrap, bridge)) return;
    return () => { void bridge.dispose(); };
  });
</script>

<svelte:head>
  <meta name="runic-hosted-bootstrap" content={bootstrap.fingerprint} />
</svelte:head>

<output data-hosted-bootstrap>{bootstrap.fingerprint}</output>
