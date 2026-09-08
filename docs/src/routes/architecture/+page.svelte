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
      preview package set releases together.
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
      title="Application Bridge keeps renderers outside the core"
      full
    >
      <p>
        Application Bridge defines the validated application boundary. Svelte,
        Vite, and native-host integrations project that boundary without making
        the application core depend on a renderer or host implementation.
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
      title="One coordinated preview package set"
    >
      <p>
        The runic-sdk monorepo supplies SDK libraries, tools and templates
        through NuGet and npm. Standalone Translations Editor distributions are
        outside this preview. Historical product and Editor archive records
        retain their original meaning and do not certify the current candidate.
        CS-WebUI remains an independent upstream compatibility product; the SDK
        includes the Runic.Application.CsWebUi integration.
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
        translator UX. Its standalone packaging and release are outside this SDK
        preview.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Preview compatibility" title="Pin the exact versions">
      <p>
        Pin exact preview versions and upgrade the Runic package set together.
        Regenerate bridge outputs and retest after upgrades. Compatibility is
        verified in the applications that consume the integrations, in frontend
        builds, and, where applicable, in NativeAOT runs.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="UI-independent contracts"
      title="Define the schema before choosing the renderer"
      full
    >
      <p>
        Effect Schema is the authority for Application Bridge wire values.
        Deterministic JSON Schema and a canonical manifest feed the
        reflection-free C# generator. UI frameworks then project validated
        application events into their own state systems without owning transport
        lifecycle.
      </p>
    </ContentCard>
  </section>
</div>
