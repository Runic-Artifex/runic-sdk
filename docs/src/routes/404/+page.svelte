<script lang="ts">
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Page not found · Runic Artifex</title>
  <meta
    name="description"
    content="The requested Runic Artifex documentation page was not found."
  />
  <meta name="robots" content="noindex" />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">404</p>
    <h1>That rune is not in the catalog.</h1>
    <p class="lede">
      The page may have moved as the preview documentation evolves.
    </p>
    <div class="actions">
      <a class="button primary" href={resolve('/')}>Return home</a>
      <a class="button secondary" href={resolve('/products')}>Browse products</a
      >
    </div>
  </section>
</main>
