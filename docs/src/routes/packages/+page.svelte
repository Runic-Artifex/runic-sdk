<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
  import {
    currentRelease,
    catalogRows,
    packageInstallCommand,
  } from '$lib/release-docs';
</script>

<svelte:head>
  <title>Package catalog · Runic Artifex</title>
  <meta
    name="description"
    content="Install published Runic SDK packages from NuGet and npm."
  />
  <meta property="og:title" content="Package catalog · Runic Artifex" />
  <meta
    property="og:description"
    content="Install published Runic SDK packages from NuGet and npm."
  />
  <meta name="twitter:title" content="Package catalog · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Install published Runic SDK packages from NuGet and npm."
  />
</svelte:head>
<div>
  <section class="page-hero shell">
    <p class="eyebrow">Packages</p>
    <h1>Install the components you need.</h1>
    <p class="lede">
      These packages are available in Runic SDK {currentRelease.version}. Keep
      Runic dependencies on the same preview version.
    </p>
  </section>
  <section class="content-grid shell">
    <ContentCard eyebrow="Public registries" title="NuGet and npm" full>
      <p>
        No GitHub package feed or token is required. Run .NET package commands
        from your project directory and npm commands from your frontend
        directory. For local .NET tools, create a manifest with <code
          >dotnet new tool-manifest</code
        > if the project does not already have one.
      </p>
      <div class="package-list">
        {#each catalogRows as row (row.name)}
          <section>
            <h2><a href={row.registryUrl} rel="external">{row.name}</a></h2>
            <p>{row.product} · {row.registry}</p>
            {#if row.name === 'Runic.Platform.Administration.Windows'}
              <p>
                Experimental Windows administration APIs. Administrative writes
                and domain services still need native validation. See the
                <a href={currentRelease.url} rel="external">release notes</a> for
                tested scenarios.
              </p>
            {/if}
            <pre><code>{packageInstallCommand(row)}</code></pre>
          </section>
        {/each}
      </div>
    </ContentCard>
  </section>
</div>
