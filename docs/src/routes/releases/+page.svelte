<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import { currentCandidate, releaseSummary } from '$lib/release-docs';
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
    <ContentCard
      eyebrow="Current SDK preview"
      title={currentCandidate.version}
      full
    >
      <p>
        SDK libraries, tools and templates come from the runic-sdk monorepo.
        Standalone Translations Editor distributions are outside this preview.
      </p>
      <p>
        Pin exact preview versions and upgrade the Runic package set together.
        Regenerate bridge outputs and retest. Preview APIs and dependency
        compatibility may change.
      </p>
      <p>
        Required manual checks use the actual local Linux system and available
        Windows VM. Other native human checks, accessibility certification and
        independent pilots are deferred before v1; automated CI and
        performance/soak gates remain required.
      </p>
      <a
        class="text-link"
        href="https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/0.2.0-preview.1.md"
        >Read the {currentCandidate.version} installation and migration guide</a
      >
    </ContentCard>
  </section>
</div>
