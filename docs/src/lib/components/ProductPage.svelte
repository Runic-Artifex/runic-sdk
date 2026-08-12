<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '$lib/components/ActionLink.svelte';
  import Notice from '$lib/components/Notice.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import * as Breadcrumb from '$lib/components/ui/breadcrumb';
  import * as Card from '$lib/components/ui/card';
  import { Separator } from '$lib/components/ui/separator';
  import type { Product } from '$lib/docs-data';

  let { product }: { product: Product } = $props();
  let isApplication = $derived(product.kind === 'application');
  let isPublished = $derived(!isApplication);
  let hasNpmPackages = $derived((product.npmPackages?.length ?? 0) > 0);
  let registryLabel = $derived(
    hasNpmPackages && product.packages.length > 0
      ? 'NuGet and npm'
      : hasNpmPackages
        ? 'npm'
        : 'NuGet',
  );
  let pageTitle = $derived(`${product.name} · Runic Artifex`);
</script>

<svelte:head>
  <title>{pageTitle}</title>
  <meta name="description" content={product.summary} />
  <meta property="og:title" content={pageTitle} />
  <meta property="og:description" content={product.summary} />
  <meta name="twitter:title" content={pageTitle} />
  <meta name="twitter:description" content={product.summary} />
</svelte:head>

<div>
  <section class="doc-hero shell">
    <Breadcrumb.Root>
      <Breadcrumb.List>
        <Breadcrumb.Item>
          <Breadcrumb.Link href={resolve('/products')}>Products</Breadcrumb.Link
          >
        </Breadcrumb.Item>
        <Breadcrumb.Separator />
        <Breadcrumb.Item>
          <Breadcrumb.Page>{product.name}</Breadcrumb.Page>
        </Breadcrumb.Item>
      </Breadcrumb.List>
    </Breadcrumb.Root>
    <div class="product-title-row">
      <span
        class="product-mark product-logo large"
        style:background-image={`url(${product.icon})`}
        aria-hidden="true"
      ></span>
      <div>
        <Badge variant="outline" class="mb-2 border-primary/30 text-primary"
          >{product.kicker}</Badge
        >
        <h1>{product.name}</h1>
      </div>
    </div>
    <p class="lede">{product.description}</p>
    <div class="actions">
      {#if isPublished}
        <ActionLink
          href={resolve('/products/[slug]#availability', {
            slug: product.slug,
          })}>Install {product.shortName}</ActionLink
        >
      {:else}
        <ActionLink href={product.source}>View source</ActionLink>
      {/if}
      {#if !isPublished && !isApplication}
        <ActionLink
          href={resolve('/products/[slug]#availability', {
            slug: product.slug,
          })}
          variant="outline">Release status</ActionLink
        >
      {/if}
      {#if isPublished}
        <ActionLink href={product.source} variant="outline"
          >View source</ActionLink
        >
      {/if}
      {#if isApplication && product.related}
        <ActionLink href={resolve(product.related.href)} variant="outline"
          >{product.related.label}</ActionLink
        >
      {/if}
      {#if product.slug === 'runic-toolkit'}
        <ActionLink href={resolve('/application-bridge')} variant="outline"
          >Explore Application Bridge</ActionLink
        >
      {/if}
      {#if product.related && !isApplication}
        <ActionLink href={resolve(product.related.href)} variant="outline"
          >{product.related.label}</ActionLink
        >
      {/if}
    </div>
  </section>

  <Separator class="shell" />
  <div class="doc-layout shell">
    <aside class="on-this-page">
      <strong>On this page</strong>
      <a href={resolve('/products/[slug]#choose', { slug: product.slug })}
        >When to choose it</a
      >
      <a href={resolve('/products/[slug]#boundaries', { slug: product.slug })}
        >Scope and boundaries</a
      >
      <a
        href={resolve('/products/[slug]#availability', {
          slug: product.slug,
        })}>Availability</a
      >
      <a href={resolve('/products/[slug]#packages', { slug: product.slug })}
        >{isApplication ? 'Downloads' : 'Packages'}</a
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
        <p class="eyebrow">Scope</p>
        <h2>Scope and boundaries</h2>
        <ul>
          {#each product.boundaries as item (item)}<li>{item}</li>{/each}
        </ul>
      </section>
      <section id="availability">
        <p class="eyebrow">Availability</p>
        <h2>
          {isApplication
            ? 'Desktop downloads'
            : isPublished
              ? `Install ${product.shortName}`
              : 'Release status'}
        </h2>
        <Notice
          title={isApplication
            ? 'First preview pending'
            : `Available on ${registryLabel}`}
        >
          <p>
            {isApplication
              ? 'The first desktop preview is pending. Downloads will appear in GitHub Releases for the editor repository.'
              : `Version ${product.version} is available on ${registryLabel}. Pin the exact preview version in reproducible applications.`}
          </p>
        </Notice>
        {#each product.install ?? [] as command (command)}<pre><code
              >{command}</code
            ></pre>{/each}
      </section>
      <section id="packages">
        <p class="eyebrow">What you get</p>
        <h2>{isApplication ? 'Downloads' : 'Packages'}</h2>
        <div class="package-list">
          {#each product.packages as name (name)}<code>{name}</code>{/each}
          {#each product.npmPackages ?? [] as name (name)}<code>{name}</code
            >{/each}
          {#each product.artifacts ?? [] as name (name)}<code>{name}</code
            >{/each}
        </div>
        <p>
          {isApplication ? 'Release status' : 'Registry version'}:
          <code>{product.version}</code>
        </p>
      </section>
      <Card.Root class="next-card" size="sm">
        <Card.Header>
          <Card.Description>Next steps</Card.Description>
          <Card.Title class="font-serif text-xl">
            <a href={resolve('/architecture')}
              >Learn how Runic products connect without coupling their cores →</a
            >
          </Card.Title>
        </Card.Header>
      </Card.Root>
    </article>
  </div>
</div>
