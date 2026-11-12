<script lang="ts">
  // Sets the Runic instance of its subtree, when it is given one, and renders a widget below it.
  import type { RunicInstance } from "@runic-artifex/views";
  import { setRunicInstance } from "../../src/views/index.js";
  import InstanceWidget from "./InstanceWidget.svelte";
  import type { InstanceCounter } from "./instance-counter.js";
  let { instance, source }: { instance: RunicInstance | undefined; source: InstanceCounter } = $props();
  // svelte-ignore state_referenced_locally
  if (instance) setRunicInstance(instance);
</script>

<InstanceWidget {source} />
