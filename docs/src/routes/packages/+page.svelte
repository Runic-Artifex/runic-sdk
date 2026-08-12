<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import * as Table from '$lib/components/ui/table';
  import { products } from '$lib/docs-data';

  const frontendIntegrations = [
    {
      name: '@runic-artifex/svelte',
      project: 'Runic Svelte',
      version: '0.1.0-preview.14.1',
    },
    {
      name: '@runic-artifex/sveltekit',
      project: 'Runic Svelte',
      version: '0.1.0-preview.14.1',
    },
    {
      name: '@runic-artifex/vite-plugin-runic-toolkit',
      project: 'Runic Vite',
      version: '0.1.0-preview.8.1',
    },
  ] as const;

  const rows = [
    ...products.flatMap((product) => [
      ...product.packages.map((name) => ({
        name,
        registry: 'NuGet',
        project: product.name,
        projectSlug: product.slug,
        version: product.version,
        availability: 'Available on NuGet',
      })),
      ...(product.npmPackages ?? []).map((name) => ({
        name,
        registry: 'npm',
        project: product.name,
        projectSlug: product.slug,
        version: product.version,
        availability: 'Available on npm',
      })),
    ]),
    ...frontendIntegrations.map((integration) => ({
      name: integration.name,
      registry: 'npm',
      project: integration.project,
      projectSlug: null,
      version: integration.version,
      availability: 'Available on npm',
    })),
  ].sort((left, right) =>
    left.registry === right.registry
      ? left.name.localeCompare(right.name)
      : left.registry.localeCompare(right.registry),
  );
</script>

<svelte:head>
  <title>Package catalog · Runic Artifex</title>
  <meta
    name="description"
    content="Browse Runic Artifex packages by registry, product, current version, and public availability."
  />
  <meta
    property="og:title"
    content="Find packages by product and registry · Runic Artifex"
  />
  <meta
    property="og:description"
    content="Browse Runic Artifex packages by registry, product, current version, and public availability."
  />
  <meta
    name="twitter:title"
    content="Find packages by product and registry · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="Browse Runic Artifex packages by registry, product, current version, and public availability."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Packages</p>
    <h1>Find packages by product and registry.</h1>
    <p class="lede">
      See which packages are available now, their exact preview versions, and
      which product owns each one.
    </p>
  </section>
  <section class="content-grid shell">
    <div class="package-table">
      <Table.Root>
        <Table.Caption class="sr-only">
          Runic Artifex package projects, current versions, and public
          availability
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head scope="col">Package</Table.Head>
            <Table.Head scope="col">Registry</Table.Head>
            <Table.Head scope="col">Project</Table.Head>
            <Table.Head scope="col">Current availability</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each rows as row (`${row.registry}:${row.name}`)}
            <Table.Row>
              <Table.Cell>{row.name}</Table.Cell>
              <Table.Cell>{row.registry}</Table.Cell>
              <Table.Cell>
                {#if row.projectSlug}
                  <a
                    href={resolve('/products/[slug]', {
                      slug: row.projectSlug,
                    })}>{row.project}</a
                  >
                {:else if row.project === 'Runic Svelte'}
                  <a href="https://github.com/Runic-Artifex/runic-svelte"
                    >Runic Svelte</a
                  >
                {:else}
                  <a href="https://github.com/Runic-Artifex/runic-vite"
                    >Runic Vite</a
                  >
                {/if}
              </Table.Cell>
              <Table.Cell>
                <span class="grid gap-1">
                  <code>{row.version}</code>
                  <span>{row.availability}</span>
                </span>
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
    <ContentCard
      eyebrow="Version policy"
      title="Preview versions stay exact"
      full
    >
      <p>
        Each repository releases one version across its package family.
        Cross-product dependencies remain exact while contracts settle;
        applications choose their own update cadence.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Compatibility"
      title="Runic Translations identifiers"
      full
    >
      <p>
        <code>RunicTranslations.*</code>, <code>runic.translations/1</code>, and
        <code>@runic-artifex/vite-plugin-runic-translations</code> are the canonical
        identifiers.
      </p>
    </ContentCard>
  </section>
</div>
