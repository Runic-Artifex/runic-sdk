<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '$lib/components/ActionLink.svelte';
  import ProductCard from '$lib/components/ProductCard.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import * as Card from '$lib/components/ui/card';
  import { Separator } from '$lib/components/ui/separator';
  import { activeProducts } from '$lib/docs-data';
  import { releaseSummary } from '$lib/release-docs';
</script>

<svelte:head>
  <title>Open-source .NET tools that work independently · Runic Artifex</title>
  <meta
    name="description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
  <meta
    property="og:title"
    content="Open-source .NET tools that work independently · Runic Artifex"
  />
  <meta
    property="og:description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
  <meta
    name="twitter:title"
    content="Open-source .NET tools that work independently · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
</svelte:head>

<div>
  <section class="hero shell">
    <div class="hero-copy">
      <p class="eyebrow">Open-source tools for .NET applications</p>
      <h1>Build with only the tools you need.</h1>
      <p class="lede">
        Runic Artifex is a family of open-source .NET tools for desktop and
        browser UI, application hosting, assets, localization, and command-line
        apps. Each product works independently and connects through documented
        integrations when needed.
      </p>
      <div class="actions">
        <ActionLink href={resolve('/products')}>Explore the products</ActionLink
        >
        <ActionLink href={resolve('/architecture')} variant="outline"
          >See how they fit together</ActionLink
        >
      </div>
    </div>
    <Card.Root
      class="hero-map"
      aria-label="Runic Artifex product relationship summary"
    >
      <div class="map-core">
        <span>Application composition</span><strong>Runic Toolkit</strong>
      </div>
      <div class="map-ring">
        <span>Bridge</span><span>Assets</span><span>Translations</span><span
          >Editor</span
        ><span>CLI</span><span>CS-WebUI</span>
      </div>
      <p>Products own their cores and official integrations.</p>
    </Card.Root>
  </section>

  <section class="section shell">
    <div class="section-heading split-heading">
      <div>
        <p class="eyebrow">Find your starting point</p>
        <h2>Start with one focused product.</h2>
        <p>
          Begin with the problem you need to solve. Add another Runic product
          only when your application needs it.
        </p>
      </div>
      <a class="text-link" href={resolve('/products')}>Compare all products →</a
      >
    </div>
    <div class="product-grid">
      {#each activeProducts as product (product.slug)}
        <ProductCard {product} label={`Explore ${product.shortName}`} />
      {/each}
    </div>
  </section>

  <section class="section shell">
    <Card.Root class="principle-panel">
      <div>
        <p class="eyebrow">Made to compose</p>
        <h2>Connect products without coupling their cores.</h2>
      </div>
      <p>
        Each product has its own repository and release cycle. Package-based
        products own their package families. Official integrations can connect a
        product to another product, framework, or tool; the product cores do not
        depend back on those integrations.
      </p>
      <p>
        Application Bridge owns its protocol and transport boundary. Frontend
        adapters project that boundary without making the application core
        depend on a renderer.
      </p>
      <ActionLink href={resolve('/architecture')} variant="outline"
        >Read the architecture guide</ActionLink
      >
    </Card.Root>
  </section>

  <Separator class="shell" />
  <section class="section shell launch-strip">
    <div>
      <Badge variant="outline" class="mb-4 border-primary/30 text-primary"
        >Public preview</Badge
      >
      <h2>Track the release authority.</h2>
      <p>
        {releaseSummary}
      </p>
    </div>
    <ActionLink href={resolve('/releases')} variant="outline"
      >Check release status</ActionLink
    >
  </section>
</div>
