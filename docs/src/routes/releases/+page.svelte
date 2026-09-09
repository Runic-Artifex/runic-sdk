<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import { currentRelease, releaseSummary } from '$lib/release-docs';
</script>

<svelte:head>
  <title>Releases · Runic Artifex</title>
  <meta
    name="description"
    content="Published Runic SDK versions, release notes and upgrade guidance."
  />
  <meta property="og:title" content="Releases · Runic Artifex" />
  <meta
    property="og:description"
    content="Published Runic SDK versions, release notes and upgrade guidance."
  />
  <meta name="twitter:title" content="Releases · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Published Runic SDK versions, release notes and upgrade guidance."
  />
</svelte:head>
<div>
  <section class="page-hero shell">
    <p class="eyebrow">Releases</p>
    <h1>Install a published SDK release.</h1>
    <p class="lede">{releaseSummary}</p>
  </section>
  <section class="content-grid shell">
    <ContentCard
      eyebrow="Published release"
      title={currentRelease.version}
      full
    >
      <p>
        SDK libraries, tools and templates are published together from the
        runic-sdk repository. Copy exact installation commands from the <a
          class="text-link"
          href={resolve('/packages')}>package catalog</a
        >, or
        <a class="text-link" href={resolve('/getting-started')}
          >create your first app</a
        >.
      </p>
      <p>
        <a class="text-link" href={currentRelease.url} rel="external"
          >Read the release notes</a
        >
        for changes, migration steps and known issues.
        <a
          class="text-link"
          href="https://github.com/Runic-Artifex/runic-sdk/releases"
          >All releases</a
        > remain available on GitHub.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Upgrading" title="Update the components you use">
      <p>
        Keep Runic packages on the same preview version, restore dependencies
        and rebuild to regenerate bridge code. Check the behavior your
        application relies on. Preview APIs can change; release notes describe
        any required migration.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Other projects" title="Separate availability">
      <p>
        The Translations Editor is a source application in the SDK repository;
        standalone distributions are not included in this release. <a
          href="https://github.com/Runic-Artifex/cs-webui/releases"
          >CS-WebUI releases</a
        > are maintained separately.
      </p>
    </ContentCard>
  </section>
</div>
