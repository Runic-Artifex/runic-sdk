<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '$lib/components/ActionLink.svelte';
  import ProductCard from '$lib/components/ProductCard.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import * as Card from '$lib/components/ui/card';
  import { Separator } from '$lib/components/ui/separator';
  import { products } from '$lib/docs-data';
</script>

<svelte:head>
  <title>Open-source .NET tools that work independently · Runic Artifex</title>
  <meta
    name="description"
    content="Open-source .NET tools for UI, hosting, workflows, assets, localization, and command-line applications."
  />
  <meta
    property="og:title"
    content="Open-source .NET tools that work independently · Runic Artifex"
  />
  <meta
    property="og:description"
    content="Open-source .NET tools for UI, hosting, workflows, assets, localization, and command-line applications."
  />
  <meta
    name="twitter:title"
    content="Open-source .NET tools that work independently · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="Open-source .NET tools for UI, hosting, workflows, assets, localization, and command-line applications."
  />
</svelte:head>

<div>
  <section class="hero shell">
    <div class="hero-copy">
      <p class="eyebrow">Open-source tools for .NET applications</p>
      <h1>Build with only the tools you need.</h1>
      <p class="lede">
        Runic Artifex is a family of open-source .NET tools for desktop and
        browser UI, application hosting, workflows, assets, localization, and
        command-line apps. Each product works independently and connects through
        documented integrations when needed.
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
        <span>Bridge</span><span>Flow</span><span>Assets</span><span
          >Translations</span
        ><span>Editor</span><span>CLI</span><span>CS-WebUI</span>
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
      {#each products as product (product.slug)}
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
      <div
        class="mini-flow"
        aria-label="Runic Flow integration dependency direction"
      >
        <span>RunicFlow</span><b>→</b><strong
          >RunicFlow.ApplicationBridge</strong
        ><b>←</b><span>RunicToolkit.ApplicationBridge</span>
      </div>
      <p>
        Runic Flow owns its Toolkit adapter because Flow defines the behavior.
        The adapter depends on both products; neither core depends back on it.
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
      <h2>The first package preview is available.</h2>
      <p>
        The repositories and their first package preview train are public on
        NuGet and npm. Runic Translations Editor remains a separate application;
        its first preview is still pending.
      </p>
    </div>
    <ActionLink href={resolve('/releases')} variant="outline"
      >Check release status</ActionLink
    >
  </section>
</div>
