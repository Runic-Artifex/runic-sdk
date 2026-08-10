<script lang="ts">
  import ProductCard from '$lib/components/ProductCard.svelte';
  import { products } from '$lib/docs-data';
</script>

<svelte:head>
  <title>Products · Runic Artifex</title>
  <meta
    name="description"
    content="Choose the independent Runic Artifex product that owns the capability you need."
  />
  <meta property="og:title" content="Products · Runic Artifex" />
  <meta
    property="og:description"
    content="Choose the independent Runic Artifex product that owns the capability you need."
  />
  <meta name="twitter:title" content="Products · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Choose the independent Runic Artifex product that owns the capability you need."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Products</p>
    <h1>Seven products, each with a clear job.</h1>
    <p class="lede">
      Every Runic product solves a focused job and can be used on its own. Add
      integrations when your application needs products to work together.
    </p>
  </section>
  <section class="shell product-grid section">
    {#each products as product (product.slug)}
      <ProductCard
        {product}
        label={`Explore ${product.shortName}`}
        headingLevel={2}
      />
    {/each}
  </section>
</div>
