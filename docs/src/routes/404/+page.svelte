<script lang="ts">
  import ErrorState from '$lib/components/ErrorState.svelte';
</script>

<svelte:head>
  <title>Page not found · Runic Artifex</title>
  <meta
    name="description"
    content="The requested Runic Artifex documentation page was not found."
  />
  <meta name="robots" content="noindex" />
</svelte:head>

<ErrorState
  status="404"
  title="That rune is not in the catalog."
  description="The page may have moved as the preview documentation evolves."
/>
