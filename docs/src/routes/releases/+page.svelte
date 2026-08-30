<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import * as Table from '$lib/components/ui/table';
  import {
    availabilityLabel,
    compatibilitySet,
    compatibilityRows,
    distributionRows,
    distributionsArePending,
    releaseRows,
    releaseSummary,
    versionLabel,
  } from '$lib/release-docs';
</script>

<svelte:head>
  <title>Release status · Runic Artifex</title>
  <meta
    name="description"
    content="See the release train, compatibility lanes, package migration status, and explicitly assigned versions."
  />
  <meta
    property="og:title"
    content="See assigned release versions · Runic Artifex"
  />
  <meta
    property="og:description"
    content="See the release train, compatibility lanes, package migration status, and explicitly assigned versions."
  />
  <meta
    name="twitter:title"
    content="See assigned release versions · Runic Artifex"
  />
  <meta
    name="twitter:description"
    content="See the release train, compatibility lanes, package migration status, and explicitly assigned versions."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Release status</p>
    <h1>See assigned release versions.</h1>
    <p class="lede">{releaseSummary}</p>
  </section>
  <section class="content-grid shell">
    <p class="eyebrow">Current availability</p>
    <div class="package-table">
      <Table.Root>
        <Table.Caption class="sr-only">
          Runic Artifex current release-train versions
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head scope="col">Product</Table.Head>
            <Table.Head scope="col">Lane</Table.Head>
            <Table.Head scope="col">Version</Table.Head>
            <Table.Head scope="col">Release status</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each releaseRows as row (`${row.lane}:${row.product}`)}
            <Table.Row>
              <Table.Cell>{row.product}</Table.Cell>
              <Table.Cell>{row.lane}</Table.Cell>
              <Table.Cell>
                <code>{versionLabel(row.version)}</code>
              </Table.Cell>
              <Table.Cell>
                {availabilityLabel(row.version)}
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
    <ContentCard
      eyebrow="1.0 profile"
      title="Language and toolchain scope is explicit"
      full
    >
      <p>
        Compatibility set <code>{compatibilitySet.id}</code> selects release
        train <code>{compatibilitySet.releaseTrainVersion}</code>. It is local
        certification input and does not authorize publication.
      </p>
      <h3>Version 1.0 language profile</h3>
      <ul>
        {#each compatibilitySet.languageProfiles.v1 as profile (`${profile.language}:${profile.role}`)}
          <li>
            <code>{profile.language}</code> — {profile.role}, {profile.state}.
          </li>
        {/each}
      </ul>
      <h3>Post-1.0 language profile</h3>
      <ul>
        {#each compatibilitySet.languageProfiles.postV1 as profile (`${profile.language}:${profile.role}`)}
          <li>
            <code>{profile.language}</code> — {profile.role}, {profile.state};
            no package or support claim is made.
          </li>
        {/each}
      </ul>
      <p>
        Exact toolchain: .NET SDK
        <code>{compatibilitySet.toolchain.dotnetSdk}</code>, Node
        <code>{compatibilitySet.toolchain.node}</code>, npm
        <code>{compatibilitySet.toolchain.npm}</code>. Selected platform
        profiles:
        {compatibilitySet.platformProfiles.join(', ')}. Native support remains
        bounded by retained platform evidence.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Compatibility"
      title="Compatibility lanes are generated from the authority"
      full
    >
      <p>
        This matrix declares the lane identities and versions selected by the
        release authority. It does not certify operating-system coverage,
        upgrade behavior, or later W70 compatibility gates.
      </p>
      <div class="package-table">
        <Table.Root>
          <Table.Caption class="sr-only">
            Runic Artifex compatibility lanes and versions
          </Table.Caption>
          <Table.Header>
            <Table.Row>
              <Table.Head scope="col">Train</Table.Head>
              <Table.Head scope="col">Lane</Table.Head>
              <Table.Head scope="col">Product</Table.Head>
              <Table.Head scope="col">Version</Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each compatibilityRows as row (`${row.train}:${row.lane}:${row.product}`)}
              <Table.Row>
                <Table.Cell>{row.train}</Table.Cell>
                <Table.Cell>{row.lane}</Table.Cell>
                <Table.Cell>{row.product}</Table.Cell>
                <Table.Cell><code>{versionLabel(row.version)}</code></Table.Cell
                >
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
      </div>
    </ContentCard>
    <ContentCard
      eyebrow="Distributions"
      title={distributionsArePending
        ? 'Release artifacts remain unassigned until published'
        : 'Distribution versions are recorded independently'}
      full
    >
      <ul>
        {#each distributionRows as distribution (distribution.identity)}
          <li>
            <code>{distribution.identity}</code> — {distribution.product},
            {distribution.kind}, {versionLabel(distribution.version)}.
          </li>
        {/each}
      </ul>
    </ContentCard>
  </section>
</div>
