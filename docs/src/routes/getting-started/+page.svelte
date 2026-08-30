<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import * as Table from '$lib/components/ui/table';
  import {
    availabilityLabel,
    choosePathRows,
    distributionRows,
    releaseSummary,
  } from '$lib/release-docs';

  const editorDistribution = distributionRows.find(
    (distribution) => distribution.productId === 'editor',
  );
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
          Native presentation for a Runic application? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-desktop' })}
            >Runic Desktop</a
          >.
        </li>
        <li>
          Direct upstream WebUI compatibility from .NET? Choose <a
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
    <div class="package-table">
      <Table.Root>
        <Table.Caption class="sr-only">
          Authority-derived paths for starting a Runic Desktop application
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head scope="col">Path</Table.Head>
            <Table.Head scope="col">Maturity</Table.Head>
            <Table.Head scope="col">Prerequisites</Table.Head>
            <Table.Head scope="col">Exact candidate packages</Table.Head>
            <Table.Head scope="col">Template or example</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each choosePathRows as row (row.path)}
            <Table.Row>
              <Table.Cell>{row.path}</Table.Cell>
              <Table.Cell>{row.maturity}</Table.Cell>
              <Table.Cell>{row.prerequisites}</Table.Cell>
              <Table.Cell>
                <span class="grid gap-1">
                  {#each row.packages as packageIdentity (packageIdentity)}
                    <code>{packageIdentity}</code>
                  {/each}
                </span>
              </Table.Cell>
              <Table.Cell>{row.start}</Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
    <ContentCard eyebrow="Release status" title="Use recorded release versions">
      <p>
        {releaseSummary} Use the package catalog to follow explicit migration targets;
        do not infer an install version from a repository branch or package name.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Application preview"
      title={`Translations Editor: ${availabilityLabel(editorDistribution?.version)}`}
    >
      <p>
        Runic Translations Editor is a separate downstream application. Its
        archive status is recorded independently from the product compatibility
        lane, so this site only offers a download when its own distribution
        version is published.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Composition" title="Connect only when needed">
      <p>
        Application Bridge keeps frontend adapters separate from the native
        application contract. Renderer packages project the validated boundary
        without making the application core depend on a UI framework.
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
