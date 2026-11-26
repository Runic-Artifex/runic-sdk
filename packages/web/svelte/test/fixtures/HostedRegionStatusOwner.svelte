<script lang="ts">
  // Two regions: the first with stalled and failed snippets, the second with its fallback alone.
  import { RunicHostedRegion } from "../../src/experimental.js";
  let { details }: { details: unknown[] } = $props();
  let first = $state<ReturnType<typeof RunicHostedRegion>>();
  export function retry(): boolean {
    return first!.retry();
  }
</script>

<RunicHostedRegion bind:this={first} name="with" onstatechange={(_state, _previous, detail) => details.push(detail)}>
  {#snippet fallback(state, status)}<p>fallback {state}{status.stalled ? " stalled" : ""}</p>{/snippet}
  {#snippet stalled()}<p>taking longer</p>{/snippet}
  {#snippet failed(status)}<button type="button" onclick={() => status.retry()}>failed: {status.failureReason}</button>{/snippet}
</RunicHostedRegion>
<RunicHostedRegion name="without">
  {#snippet fallback(state, status)}<p>fallback {state}{status.stalled ? " stalled" : ""} {status.failureReason}</p>{/snippet}
</RunicHostedRegion>
