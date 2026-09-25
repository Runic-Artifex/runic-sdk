<script lang="ts">
  import type { HomePageReference } from "../../Frontend/src/generated/home.js";
  import { pageState } from "./bridge-state.js";

  let { page }: { page: HomePageReference } = $props();
  const home = pageState(() => page);
</script>

{#if home.state}
  <h1>{home.state.greeting}</h1>
  <p class="muted">The sidebar stays mounted while this content changes.</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if home.error}<p role="alert">{String(home.error)}</p>{/if}
