<script lang="ts">
  import { resolve } from '$app/paths';
  import { products } from '$lib/docs-data';
</script>

<svelte:head>
  <title>Products · Runic Artifex</title>
  <meta
    name="description"
    content="Choose the independent Runic Artifex product that owns the capability you need."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Product map</p>
    <h1>Independent products. Designed to compose.</h1>
    <p class="lede">
      Start at the narrowest boundary that solves your problem. Add Runic
      Toolkit only when you need the shared application-composition experience.
    </p>
  </section>
  <section class="shell product-grid section">
    {#each products as product (product.slug)}
      <a
        class="product-card"
        href={resolve('/products/[slug]', { slug: product.slug })}
      >
        <span
          class="product-mark product-logo"
          style:background-image={`url(${product.icon})`}
          aria-hidden="true"
        ></span>
        <p class="kicker">{product.kicker}</p>
        <h3>{product.name}</h3>
        <p>{product.summary}</p>
        <span class="card-link"
          >{product.kind === 'application'
            ? 'Editor and boundaries'
            : 'Packages and boundaries'} →</span
        >
      </a>
    {/each}
  </section>
</main>
