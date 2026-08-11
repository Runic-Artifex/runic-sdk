<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
</script>

<svelte:head>
  <title>Getting started · Runic Artifex</title>
  <meta
    name="description"
    content="Choose the focused Runic Artifex product that solves your next application problem."
  />
  <meta property="og:title" content="Getting started · Runic Artifex" />
  <meta
    property="og:description"
    content="Choose the focused Runic Artifex product that solves your next application problem."
  />
  <meta name="twitter:title" content="Getting started · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Choose the focused Runic Artifex product that solves your next application problem."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Getting started</p>
    <h1>Start from what you’re building.</h1>
    <p class="lede">
      Runic Artifex is not one mandatory stack. Start with one product, then add
      an official integration when it needs to work with another product.
    </p>
  </section>
  <section class="content-grid shell">
    <ContentCard eyebrow="Getting started" title="What are you building?" full>
      <ul>
        <li>
          A lightweight desktop window? Start with <a
            href={resolve('/products/[slug]', { slug: 'cs-webui' })}>CS-WebUI</a
          >.
        </li>
        <li>
          One application across desktop and browser? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-toolkit' })}
            >Runic Toolkit</a
          >.
        </li>
        <li>
          Long-running or coordinated work? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-flow' })}
            >Runic Flow</a
          >.
        </li>
        <li>
          Portable static assets? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-assets' })}
            >Runic Assets</a
          >.
        </li>
        <li>
          Localization contracts and builds? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-translations' })}
            >Runic Translations</a
          >.
        </li>
        <li>
          A translator-facing workspace? Use <a
            href={resolve('/products/[slug]', {
              slug: 'runic-translations-editor',
            })}>Runic Translations Editor</a
          >.
        </li>
        <li>
          A NativeAOT command-line application? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-command-line' })}
            >Runic Command Line</a
          >.
        </li>
      </ul>
    </ContentCard>
    <ContentCard eyebrow="Available today" title="Try CS-WebUI">
      <p>
        CS-WebUI is the only Runic package family currently available on NuGet.
        It includes <code>CsWebUi</code> and <code>CsWebUi.Native</code>.
      </p>
      <pre><code>dotnet add package CsWebUi --version 2.5.0-beta.4.4</code
        ></pre>
    </ContentCard>
    <ContentCard
      eyebrow="Other products"
      title="Browse the source while packages are pending"
    >
      <p>
        The remaining package families have exact verified candidates, and their
        source is public. Install commands will appear after matching registry
        artifacts are published and accepted. Runic Translations Editor is a
        separate application; its first preview is still pending.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Composition" title="Connect only when needed">
      <p>
        Runic Flow stays headless and frontend-neutral. When Flow needs to
        connect to Runic Toolkit, Flow owns that adapter. The package is named
        <code>RunicFlow.ApplicationBridge</code>; it depends on both products
        while neither core depends back on it.
      </p>
    </ContentCard>
    <ContentCard eyebrow="During preview" title="Keep versions explicit">
      <p>
        Pin exact preview versions. Products release independently, so update
        each one on the cadence your application needs.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Continue" title="Go deeper" full>
      <p>
        <a class="text-link" href={resolve('/products')}>Compare products</a>,
        read the
        <a class="text-link" href={resolve('/application-bridge')}
          >Application Bridge guide</a
        >, or check
        <a class="text-link" href={resolve('/packages')}>package availability</a
        >.
      </p>
    </ContentCard>
  </section>
</div>
