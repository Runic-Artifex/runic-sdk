<script lang="ts">
  import { resolve } from '$app/paths';
  import { products } from '$lib/docs-data';
</script>

<svelte:head>
  <title>Build one application model across .NET surfaces · Runic Artifex</title
  >
  <meta
    name="description"
    content="Runic Artifex is a family of independent, NativeAOT-minded tools for application architecture, UI, orchestration, assets, localization, and command lines."
  />
</svelte:head>

<main>
  <section class="hero shell">
    <div class="hero-copy">
      <p class="eyebrow">Runic Artifex ecosystem</p>
      <h1>Build one application model across .NET surfaces.</h1>
      <p class="lede">
        Independent tools for WebUI, desktop, application bridges, headless
        orchestration, assets, localization, and command lines—designed to
        compose without sharing one release train.
      </p>
      <div class="actions">
        <a class="button primary" href={resolve('/getting-started')}
          >Start with the preview</a
        >
        <a class="button secondary" href={resolve('/architecture')}
          >Understand the boundaries</a
        >
      </div>
    </div>
    <div
      class="hero-map"
      aria-label="Runic Artifex product relationship summary"
    >
      <div class="map-core">
        <span>Application composition</span><strong>Runic Toolkit</strong>
      </div>
      <div class="map-ring">
        <span>Bridge</span><span>Flow</span><span>Assets</span><span
          >Translations</span
        ><span>Editor</span><span>CLI</span><span>CsWebUi</span>
      </div>
      <p>Products own their cores and official integrations.</p>
    </div>
  </section>

  <section class="section shell">
    <div class="section-heading split-heading">
      <div>
        <p class="eyebrow">Choose your layer</p>
        <h2>Use one product or compose the ecosystem.</h2>
      </div>
      <a class="text-link" href={resolve('/products')}>Compare all products →</a
      >
    </div>
    <div class="product-grid">
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
          <span class="card-link">Explore {product.shortName} →</span>
        </a>
      {/each}
    </div>
  </section>

  <section class="section shell">
    <div class="principle-panel">
      <div>
        <p class="eyebrow">The ownership rule</p>
        <h2>Integrations belong to the product doing the integrating.</h2>
      </div>
      <div
        class="mini-flow"
        aria-label="Runic Flow integration dependency direction"
      >
        <span>RunicFlow</span><b>→</b><strong
          >RunicFlow.ApplicationBridge</strong
        ><b>←</b><span>RunicToolkit.ApplicationBridge</span>
      </div>
      <p>
        Cores remain portable. Adapters depend on both sides. Git history,
        versioning, and release decisions stay with the product that owns the
        behavior.
      </p>
    </div>
  </section>

  <section class="section shell launch-strip">
    <div>
      <p class="eyebrow">First public preview</p>
      <h2>The source is public; the first package previews are next.</h2>
      <p>
        Current main branches and exact candidate artifacts are green. Public
        registry publication remains gated on trusted publishers and final
        registry acceptance checks.
      </p>
    </div>
    <a class="button secondary" href={resolve('/releases')}
      >See current release status</a
    >
  </section>
</main>
