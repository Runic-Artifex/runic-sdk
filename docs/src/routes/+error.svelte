<script lang="ts">
  import { resolve } from '$app/paths';
  import { page } from '$app/state';
</script>

<svelte:head
  ><title
    >{page.status === 404 ? 'Page not found' : 'Error'} · Runic Artifex</title
  ></svelte:head
>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">{page.status}</p>
    <h1>
      {page.status === 404
        ? 'That rune is not in the catalog.'
        : 'The page could not be rendered.'}
    </h1>
    <p class="lede">
      {page.status === 404
        ? 'The page may have moved as the preview documentation evolves.'
        : 'Please return to the documentation home and try again.'}
    </p>
    <div class="actions">
      <a class="button primary" href={resolve('/')}>Return home</a>
      <a class="button secondary" href={resolve('/products')}>Browse products</a
      >
    </div>
  </section>
</main>
