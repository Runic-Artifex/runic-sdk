<script lang="ts">
  import { RunicHostedRegion } from "../../src/experimental.js";
  let { states }: { states: string[] } = $props();
</script>

<RunicHostedRegion name="check" sizing="content-height" label="System check" class="step" onstatechange={state => states.push(state)}>
  {#snippet fallback(state)}<p>fallback {state}</p>{/snippet}
</RunicHostedRegion>
