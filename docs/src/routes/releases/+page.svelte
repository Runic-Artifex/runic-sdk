<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import * as Table from '$lib/components/ui/table';

  const train = [
    ['CS-WebUI', '2.5.0-beta.4.4', 'Available on NuGet', 'Available now'],
    [
      'Runic Command Line',
      '0.1.0-preview.5.1',
      'Available on NuGet',
      'Published independently',
    ],
    [
      'Runic Translations',
      '0.1.0-preview.8.1',
      'Available on NuGet and npm',
      'Published independently',
    ],
    [
      'Runic Translations Editor',
      null,
      'First preview pending',
      'Follows Runic Translations',
    ],
    [
      'Runic Svelte integrations',
      '0.1.0-preview.14.1',
      'Available on npm',
      'Published before Runic Toolkit',
    ],
    [
      'Runic Vite integration',
      '0.1.0-preview.8.1',
      'Available on npm',
      'Published before Runic Toolkit',
    ],
    [
      'Runic Toolkit',
      '0.1.0-preview.30.1',
      'Available on NuGet and npm',
      'Published after Svelte and Vite integrations',
    ],
    [
      'Runic Assets',
      '0.1.0-preview.24.1',
      'Available on NuGet',
      'Published after Runic Toolkit',
    ],
    [
      'Runic Flow',
      '0.1.0-preview.19.1',
      'Available on NuGet',
      'Published after Runic Toolkit',
    ],
  ] as const;
</script>

<svelte:head>
  <title>Releases · Runic Artifex</title>
  <meta
    name="description"
    content="See which Runic Artifex packages are publicly available and which application preview is still pending."
  />
  <meta
    property="og:title"
    content="See what you can install today. · Runic Artifex"
  />
  <meta
    property="og:description"
    content="See which Runic Artifex packages are publicly available and which application preview is still pending."
  />
  <meta
    name="twitter:title"
    content="See what you can install today. · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="See which Runic Artifex packages are publicly available and which application preview is still pending."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Release status</p>
    <h1>See what you can install today.</h1>
    <p class="lede">
      CS-WebUI and the first Runic Artifex package preview train are available
      on NuGet and npm. Translations Editor’s first preview is still pending.
    </p>
  </section>
  <section class="content-grid shell">
    <p class="eyebrow">Current availability</p>
    <div class="package-table">
      <Table.Root>
        <Table.Caption class="sr-only">
          Runic Artifex package availability and first preview publication order
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head scope="col">Product</Table.Head>
            <Table.Head scope="col">Current availability</Table.Head>
            <Table.Head scope="col">Release relationship</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each train as [name, version, availability, constraint] (name)}
            <Table.Row>
              <Table.Cell>{name}</Table.Cell>
              <Table.Cell>
                <span class="grid gap-1">
                  {#if version}<code>{version}</code>{/if}
                  <span>{availability}</span>
                </span>
              </Table.Cell>
              <Table.Cell>{constraint}</Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
    <ContentCard
      eyebrow="Release integrity"
      title="Verify once. Publish the same artifacts."
      full
    >
      <p>
        Candidates are built and tested from exact public source commits.
        Registry publishing uses the same verified artifacts and remains
        protected by trusted-publisher configuration and final registry
        acceptance.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Preview policy"
      title="Expect exact versions and independent releases"
      full
    >
      <p>
        Products keep separate repositories and release cycles. Cross-product
        integrations pin exact versions during preview, and applications choose
        when to update.
      </p>
    </ContentCard>
  </section>
</div>
