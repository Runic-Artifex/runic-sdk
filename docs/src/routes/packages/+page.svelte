<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import * as Table from '$lib/components/ui/table';
  import {
    availabilityLabel,
    catalogRows,
    migrationRows,
    packageInstallCommand,
    versionLabel,
  } from '$lib/release-docs';
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
      Browse canonical package identities and their authority-owned release
      status. Historical migrations stay outside the current package catalog.
    </p>
  </section>
  <section class="content-grid shell">
    <div class="package-table">
      <Table.Root>
        <Table.Caption class="sr-only">
          Runic Artifex canonical package identities and release versions
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head scope="col">Package</Table.Head>
            <Table.Head scope="col">Registry</Table.Head>
            <Table.Head scope="col">Project</Table.Head>
            <Table.Head scope="col">Release status</Table.Head>
            <Table.Head scope="col">Authority state</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each catalogRows as row (`${row.registry}:${row.name}`)}
            <Table.Row>
              <Table.Cell>{row.name}</Table.Cell>
              <Table.Cell>{row.registry}</Table.Cell>
              <Table.Cell>{row.product}</Table.Cell>
              <Table.Cell>
                <span class="grid gap-1">
                  <code>{versionLabel(row.version)}</code>
                  <span>{availabilityLabel(row.version)}</span>
                  {#if packageInstallCommand(row)}
                    <span
                      >Install: <code>{packageInstallCommand(row)}</code></span
                    >
                  {/if}
                </span>
              </Table.Cell>
              <Table.Cell>{row.state}</Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
    {#if migrationRows.length > 0}
      <div class="package-table">
        <Table.Root>
          <Table.Caption class="sr-only">
            Runic Artifex legacy package migration inventory
          </Table.Caption>
          <Table.Header>
            <Table.Row>
              <Table.Head scope="col">Legacy source identity</Table.Head>
              <Table.Head scope="col">Registry</Table.Head>
              <Table.Head scope="col">Disposition</Table.Head>
              <Table.Head scope="col">Migration</Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each migrationRows as row (`${row.registry}:${row.source}`)}
              <Table.Row>
                <Table.Cell><code>{row.source}</code></Table.Cell>
                <Table.Cell>{row.registry}</Table.Cell>
                <Table.Cell>
                  <code>{row.disposition}</code>
                  {#if row.target}
                    <span class="block text-muted-foreground"
                      >Canonical target: <code>{row.target}</code></span
                    >
                  {/if}
                </Table.Cell>
                <Table.Cell>
                  <code>{row.migrationKind}</code>
                  {#if row.migrationTarget}
                    <span class="block text-muted-foreground"
                      >Target: <code>{row.migrationTarget}</code></span
                    >
                  {/if}
                  <span class="block text-muted-foreground">{row.guidance}</span
                  >
                </Table.Cell>
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
      </div>
    {/if}
    <ContentCard
      eyebrow="Version policy"
      title="Versions are assigned by the release train"
      full
    >
      <p>
        The release manifest is the source of this catalog. A package without a
        published train version remains explicitly unassigned; this site never
        guesses a version from a repository or registry.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Compatibility" title="Migration is explicit" full>
      <p>
        Legacy entries record their disposition and the independent migration
        kind, target, and guidance. A retired source can still direct consumers
        to a replacement package without becoming a forwarding package.
      </p>
    </ContentCard>
  </section>
</div>
