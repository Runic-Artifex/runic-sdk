<script lang="ts">
  import type { HomePageReference } from "../../Frontend/src/generated/home.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  let { page }: { page: HomePageReference } = $props();
  const home = useView(() => page);
</script>

{#if home.state}
  <h1>{home.state.greeting}</h1>
  <p>Open the document to see two Views of one Editor ViewModel.</p>
{:else}
  <p>Connecting…</p>
{/if}
{#if home.error}<p role="alert">{String(home.error)}</p>{/if}
