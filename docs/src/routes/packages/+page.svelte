<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import { currentCandidate, candidateCatalogRows } from '$lib/release-docs';
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
      status. Package versions remain candidates until publication is verified.
    </p>
  </section>
  <section class="content-grid shell">
    <ContentCard
      eyebrow="Current SDK preview"
      title={`${currentCandidate.version} — unpublished`}
      full
    >
      <p>
        The workspace selects these SDK packages. Upgrade exact preview versions
        together, regenerate bridge outputs and retest. Standalone Editor
        distributions are outside this preview.
      </p>
      <ul>
        {#each candidateCatalogRows as row (row.name)}
          <li>
            <code>{row.name}@{row.version}</code> — {row.registry}, unpublished
            candidate
          </li>
        {/each}
      </ul>
      <a
        class="text-link"
        href="https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/0.2.0-preview.1.md"
        >Preview installation and migration guide</a
      >
    </ContentCard>
  </section>
</div>
