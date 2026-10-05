<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '#lib/components/ActionLink.svelte';
  import CreatorHero from '#lib/components/CreatorHero.svelte';
  import ProductCard from '#lib/components/ProductCard.svelte';
  import { Badge } from '#lib/components/ui/badge/index.js';
  import * as Card from '#lib/components/ui/card/index.js';
  import { Separator } from '#lib/components/ui/separator/index.js';
  import { activeProducts } from '#lib/docs-data.js';
  import { currentRelease, releaseSummary } from '#lib/release-docs.js';
</script>

<svelte:head>
  <title>Runic SDK for .NET and TypeScript applications · Runic Artifex</title>
  <meta
    name="description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
  <meta
    property="og:title"
    content="Runic SDK for .NET and TypeScript applications · Runic Artifex"
  />
  <meta
    property="og:description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
  <meta
    name="twitter:title"
    content="Runic SDK for .NET and TypeScript applications · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications."
  />
</svelte:head>

<div>
  <CreatorHero version={currentRelease.version} />

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
        The Runic SDK repository contains SDK libraries, tools, templates and
        examples. Its package set releases together through NuGet and npm.
        Command Line and Translations, including Translations Editor, will
        release independently. Official integrations can connect a product to
        another product, framework, or tool; the product cores do not depend
        back on those integrations.
      </p>
      <p>
        Application Views defines explicit Window and View contracts and emits
        ordinary TypeScript clients. Frontend frameworks render their
        components; .NET owns logical Views and ViewModel scopes.
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
        >Published SDK</Badge
      >
      <h2>Install the published SDK.</h2>
      <p>
        {releaseSummary}
      </p>
    </div>
    <ActionLink href={resolve('/releases')} variant="outline"
      >Read release notes</ActionLink
    >
  </section>
</div>
