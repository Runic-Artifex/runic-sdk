<script lang="ts">
  import { page } from '$app/state';
  import ErrorState from '$lib/components/ErrorState.svelte';
</script>

<svelte:head
  ><title
    >{page.status === 404 ? 'Page not found' : 'Error'} · Runic Artifex</title
  ></svelte:head
>

<ErrorState
  status={page.status}
  title={page.status === 404
    ? 'That rune is not in the catalog.'
    : 'The page could not be rendered.'}
  description={page.status === 404
    ? 'The page may have moved as the preview documentation evolves.'
    : 'Please return to the documentation home and try again.'}
/>
