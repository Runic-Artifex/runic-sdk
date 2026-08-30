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
    distributionRows,
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
  let distributions = $derived(
    distributionRows.filter(
      (entry) => entry.productId === product.releaseProduct,
    ),
  );
  let currentDistributions = $derived(
    distributions.filter(
      (distribution) => distribution.kind !== 'application-archive',
    ),
  );
  let historicalDistributions = $derived(
    distributions.filter(
      (distribution) => distribution.kind === 'application-archive',
    ),
  );
  let availabilityVersion = $derived(
    isApplication ? currentDistributions[0]?.version : productVersion,
  );
  let hasPublishedVersion = $derived(
    isApplication
      ? currentDistributions.some(
          (distribution) => distribution.version.state === 'published',
        )
      : availabilityVersion?.state === 'published',
  );
  let packageSectionTitle = $derived(
    isApplication && currentDistributions.length === 0
      ? 'Distribution history'
      : isApplication
        ? 'Downloads'
        : 'Packages',
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
        <h2>
          {isArchived
            ? 'Archive status'
            : isIndependent
              ? 'Independent compatibility product'
              : isApplication
                ? currentDistributions.length > 0
                  ? 'Desktop downloads'
                  : 'Release status'
                : hasPublishedVersion
                  ? `Install ${product.shortName}`
                  : 'Release status'}
        </h2>
        <Notice
          title={isArchived
            ? 'Archived — no release-bearing packages'
            : isIndependent
              ? 'Maintained outside the Runic v1 train'
              : isApplication
                ? hasPublishedVersion
                  ? 'Published distributions available'
                  : 'Distribution versions unassigned'
                : hasPublishedVersion
                  ? `Version ${availabilityVersion?.value}`
                  : 'Version unassigned'}
        >
          <p>
            {#if isArchived}
              This product is archived. Historical records retain its former
              identities for removal guidance, while current release authority
              contains no public replacement or forwarding package.
              {#if product.archive}
                Archive evidence: <code
                  >{product.archive.evidence.repository}</code
                >
                at <code>{product.archive.evidence.revision}</code>,
                <code>{product.archive.evidence.path}</code>.
              {/if}
            {:else if isIndependent}
              This product is maintained and released by its own repository. It
              is not governed by the Runic v1 compatibility set, so this site
              does not infer its package availability or version. Consult the
              source repository for its current packages and upstream WebUI
              compatibility.
            {:else if isApplication}
              {#if currentDistributions.length > 0}
                Each desktop distribution has its own release status. Only a
                distribution with a recorded published version is available.
              {:else}
                No current desktop distribution is recorded. Any archived
                distribution evidence is listed separately below and does not
                indicate current availability.
              {/if}
            {:else if hasPublishedVersion}
              Version {availabilityVersion?.value} is published for this product's
              active compatibility lane.
            {:else}
              The release authority has not assigned a version. This
              documentation does not infer availability from repository state.
            {/if}
          </p>
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
            {#each currentDistributions as distribution (distribution.identity)}
              <span>
                <code>{distribution.identity}</code>
                {#if distribution.version.state === 'published'}
                  — Published distribution: <code
                    >{distribution.version.value}</code
                  >
                {:else}
                  — Distribution version unassigned
                {/if}
              </span>
            {/each}
            {#each historicalDistributions as distribution (distribution.identity)}
              <span>
                Historical archive distribution:
                <code>{distribution.identity}</code>
                {#if distribution.version.state === 'published'}
                  — Published historical distribution: <code
                    >{distribution.version.value}</code
                  >
                {:else}
                  — Historical distribution version unassigned
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
