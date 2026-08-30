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
      integrations stay in separate packages and are released with the product
      that owns their behavior.
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
      eyebrow="Independent releases"
      title="Products move at their own pace"
    >
      <p>
        Assets, Translations, Translations Editor, Command Line, Toolkit, and
        CS-WebUI each keep an independent release history. Package families
        release registry artifacts; Translations Editor releases desktop
        archives from its own repository.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Integration ownership"
      title="Behavior stays with the product that defines it"
    >
      <p>
        <code>Runic.Assets</code> lives and releases with Assets. The same rule applies
        when a product connects to an external framework or development tool.
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
        translator UX, packaging, and releases.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Preview compatibility" title="Pin the exact versions">
      <p>
        Integrations pin exact cross-product versions during the preview.
        Compatibility is verified in the applications that consume the
        integrations, in frontend builds, and, where applicable, in NativeAOT
        runs.
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
