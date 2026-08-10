<script lang="ts">
  import { resolve } from '$app/paths';
  import type { Product } from '$lib/docs-data';

  let { product }: { product: Product } = $props();
  let isApplication = $derived(product.kind === 'application');
</script>

<svelte:head>
  <title>{product.name} · Runic Artifex</title>
  <meta name="description" content={product.summary} />
</svelte:head>

<main>
  <section class="doc-hero shell">
    <div class="breadcrumb">
      <a href={resolve('/products')}>Products</a><span>/</span><span
        >{product.name}</span
      >
    </div>
    <div class="product-title-row">
      <span
        class="product-mark product-logo large"
        style:background-image={`url(${product.icon})`}
        aria-hidden="true"
      ></span>
      <div>
        <p class="eyebrow">{product.kicker}</p>
        <h1>{product.name}</h1>
      </div>
    </div>
    <p class="lede">{product.description}</p>
    <div class="actions">
      <a
        class="button primary"
        href={`https://github.com/Runic-Artifex/${product.slug}`}>View source</a
      >
      {#if product.slug === 'runic-toolkit'}
        <a class="button secondary" href={resolve('/application-bridge')}
          >Application Bridge guide</a
        >
      {/if}
      {#if product.related}
        <a class="button secondary" href={resolve(product.related.href)}
          >{product.related.label}</a
        >
      {/if}
      {#if !isApplication}<a
          class="button secondary"
          href={resolve('/packages')}>Package catalog</a
        >{/if}
    </div>
  </section>

  <div class="doc-layout shell">
    <aside class="on-this-page">
      <strong>On this page</strong>
      <a href={resolve('/products/[slug]#choose', { slug: product.slug })}
        >When to choose it</a
      >
      <a href={resolve('/products/[slug]#boundaries', { slug: product.slug })}
        >Boundaries</a
      >
      <a href={resolve('/products/[slug]#install', { slug: product.slug })}
        >{isApplication ? 'Release' : 'Install'}</a
      >
      <a href={resolve('/products/[slug]#packages', { slug: product.slug })}
        >{isApplication ? 'Artifacts' : 'Packages'}</a
      >
    </aside>
    <article class="doc-content">
      <section id="choose">
        <p class="eyebrow">Fit</p>
        <h2>When to choose it</h2>
        <ul class="check-list">
          {#each product.bestFor as item (item)}<li>{item}</li>{/each}
        </ul>
      </section>
      <section id="boundaries">
        <p class="eyebrow">Architecture</p>
        <h2>What stays outside</h2>
        <ul>
          {#each product.boundaries as item (item)}<li>{item}</li>{/each}
        </ul>
      </section>
      <section id="install">
        <p class="eyebrow">Preview</p>
        <h2>{isApplication ? 'Download a release' : 'Prepare to install'}</h2>
        <div class="notice">
          <strong
            >{isApplication
              ? 'First release pending'
              : 'Publication pending'}</strong
          >
          <p>
            {isApplication
              ? 'The editor packaging pipeline produces self-contained desktop archives. Signed public downloads will appear in the editor repository without coupling its release cadence to the translation package family.'
              : 'Candidate versions are being refreshed and verified from the final public source commits. Install commands will be added when the matching registry artifacts are ready.'}
          </p>
        </div>
        {#each product.install ?? [] as command (command)}<pre><code
              >{command}</code
            ></pre>{/each}
      </section>
      <section id="packages">
        <p class="eyebrow">Owned surface</p>
        <h2>{isApplication ? 'Release artifacts' : 'Package family'}</h2>
        <div class="package-list">
          {#each product.packages as name (name)}<code>{name}</code>{/each}
          {#each product.npmPackages ?? [] as name (name)}<code>{name}</code
            >{/each}
          {#each product.artifacts ?? [] as name (name)}<code>{name}</code
            >{/each}
        </div>
        <p>
          {isApplication ? 'Release status' : 'Public candidate status'}:
          <code>{product.version}</code>
        </p>
      </section>
      <div class="next-card">
        <span>Next</span>
        <a href={resolve('/architecture')}
          >See how product integrations preserve dependency direction →</a
        >
      </div>
    </article>
  </div>
</main>
