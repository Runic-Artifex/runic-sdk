<script lang="ts">
  import ContentCard from '$lib/components/ContentCard.svelte';
</script>

<svelte:head>
  <title>Architecture · Runic Artifex</title>
  <meta
    name="description"
    content="How Runic products stay useful on their own while official integrations let you connect only the pieces your project needs."
  />
  <meta property="og:title" content="Architecture · Runic Artifex" />
  <meta
    property="og:description"
    content="How Runic products stay useful on their own while official integrations let you connect only the pieces your project needs."
  />
  <meta name="twitter:title" content="Architecture · Runic Artifex" />
  <meta
    name="twitter:description"
    content="How Runic products stay useful on their own while official integrations let you connect only the pieces your project needs."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Architecture</p>
    <h1>Use products independently. Connect them deliberately.</h1>
    <p class="lede">
      Each Runic tool works on its own. Integrations connect products, external
      frameworks, or tooling without making a core depend back on them. Those
      integrations stay in separate packages within the Runic SDK monorepo. The
      SDK package set releases together.
    </p>
  </section>
  <section class="content-grid shell">
    <ContentCard
      eyebrow="Why this matters"
      title="Add an integration without adopting a stack"
      full
    >
      <p>
        Choose one product first. When two products need to meet, add the
        integration that owns that behavior; neither core changes direction.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="A concrete example"
      title="Windows and Views keep rendering in the frontend"
      full
    >
      <p>
        Runic Application Views defines explicit .NET Window and View contracts
        and generates ordinary TypeScript clients. Svelte, React, Vue and Angular
        render those contracts with their own components; the application model
        does not own a framework's visual tree.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Portable cores" title="Cores stay portable">
      <p>
        Runic Assets owns no host. Runic Translations begins with .NET but
        defines language-neutral contracts.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="SDK release boundary"
      title="One coordinated SDK package set"
    >
      <p>
        The runic-sdk monorepo supplies SDK libraries, tools and templates
        through NuGet and npm. Translations Editor is available from source. All
        package families share the SDK release workflow. CS-WebUI remains an
        independent upstream compatibility product; the SDK includes the
        Runic.Application.Views integrations.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Integration ownership"
      title="Behavior stays with the product that defines it"
    >
      <p>
        <code>Runic.Assets</code> owns asset behavior within the SDK monorepo. Package
        boundaries preserve that ownership when a product connects to an external
        framework or development tool.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Application boundary"
      title="The editor uses the translation system"
      full
    >
      <p>
        Runic Translations owns schemas, compiler behavior, runtime contracts,
        generators, and authoring APIs. Runic Translations Editor uses its
        packages and APIs as a downstream desktop application and owns
        translator UX. It is available from source; the SDK package catalog does
        not include an Editor distribution.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Preview compatibility" title="Pin the exact versions">
      <p>
        Pin exact preview versions and upgrade the Runic package set together.
        Rebuild generated Window and View clients and retest after upgrades.
        Compatibility is verified in consuming applications, frontend builds,
        and, where applicable, NativeAOT runs.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="UI-independent contracts"
      title="Define the schema before choosing the renderer"
      full
    >
      <p>
        Explicit partial Window and View types are the contract authority.
        The build inspects the compiled application after MVVM generators run and
        emits C# attachments and ordinary TypeScript modules. UI frameworks own
        rendering while .NET scopes ViewModels, logical Views, commands, and
        operations.
      </p>
    </ContentCard>
  </section>
</div>
