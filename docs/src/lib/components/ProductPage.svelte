<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '$lib/components/ActionLink.svelte';
  import Notice from '$lib/components/Notice.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import * as Breadcrumb from '$lib/components/ui/breadcrumb';
  import * as Card from '$lib/components/ui/card';
  import { Separator } from '$lib/components/ui/separator';
  import type { Product } from '$lib/docs-data';
  import {
    catalogRows,
    currentCandidate,
    packageInstallCommand,
    versionLabel,
  } from '$lib/release-docs';

  let { product }: { product: Product } = $props();
  let isApplication = $derived(product.kind === 'application');
  let isArchived = $derived(product.availability === 'archived');
  let isIndependent = $derived(product.availability === 'independent');
  let productVersion = $derived({
    state: product.versionState,
    value: product.version,
  });
  let currentPackages = $derived(
    catalogRows.filter((entry) => entry.productId === product.releaseProduct),
  );
  let hasPublishedVersion = $derived(productVersion.state === 'published');
  let availabilityVersion = $derived(productVersion);
  let packageSectionTitle = $derived(
    isApplication ? 'Source application' : 'Packages',
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
      {#if !isArchived && hasPublishedVersion}
        <ActionLink
          href={resolve('/products/[slug]#availability', {
            slug: product.slug,
          })}
          >{isApplication
            ? 'Downloads'
            : `Install ${product.shortName}`}</ActionLink
        >
      {:else}
        <ActionLink href={product.source}>View source</ActionLink>
      {/if}
      {#if !isArchived && !isIndependent && !hasPublishedVersion && !isApplication}
        <ActionLink
          href={resolve('/products/[slug]#availability', {
            slug: product.slug,
          })}
          variant="outline">Release status</ActionLink
        >
      {/if}
      {#if !isArchived && hasPublishedVersion}
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
        })}
        >{isArchived
          ? 'Archive status'
          : isIndependent
            ? 'Independent status'
            : 'Availability'}</a
      >
      {#if !isArchived && !isIndependent}
        <a href={resolve('/products/[slug]#packages', { slug: product.slug })}
          >{packageSectionTitle}</a
        >
      {/if}
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
        <h2>Release status</h2>
        <Notice
          title={isIndependent
            ? 'External WebUI binding'
            : isApplication
              ? 'Source application'
              : `${currentCandidate.version} — unpublished`}
        >
          <p>
            {#if isIndependent}
              CS-WebUI is maintained separately. The SDK's
              Runic.Application.CsWebUi adapter shares application APIs while
              reporting native platform services unavailable.
            {:else if isApplication}
              Standalone Translations Editor distributions are outside this SDK
              preview. Build and run the application from this repository.
            {:else}
              These packages are part of the unpublished SDK candidate. A
              package version in source does not establish registry
              availability.
            {/if}
          </p>
          <a
            class="text-link"
            href="https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/0.2.0-preview.1.md"
            >Preview installation and migration guide</a
          >
        </Notice>
      </section>
      {#if !isArchived && !isIndependent}
        <section id="packages">
          <p class="eyebrow">What you get</p>
          <h2>{packageSectionTitle}</h2>
          <div class="package-list">
            {#each currentPackages as entry (entry.name)}
              <span>
                <code>{entry.name}</code>
                {#if packageInstallCommand(entry)}
                  — Install: <code>{packageInstallCommand(entry)}</code>
                {:else}
                  — <code>{versionLabel(entry.version)}</code>
                {/if}
              </span>
            {/each}
          </div>
          {#if !isApplication}
            <p>
              Release-train version:
              <code>{availabilityVersion?.value ?? 'Version unassigned'}</code>
            </p>
          {/if}
        </section>
      {/if}
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
