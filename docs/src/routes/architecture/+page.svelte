<svelte:head>
  <title>Architecture · Runic Artifex</title>
  <meta
    name="description"
    content="The ownership and dependency rules that keep Runic Artifex products independent."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Architecture</p>
    <h1>Ownership and dependency direction are separate decisions.</h1>
    <p class="lede">
      A product owns the behavior it contributes. An integration depends on both
      cores. Neither core depends back on the integration.
    </p>
  </section>
  <section class="content-grid shell">
    <article class="info-card full">
      <p class="eyebrow">Canonical seam</p>
      <h2>Flow controls its Toolkit integration</h2>
      <div class="mini-flow">
        <span>RunicFlow<br /><small>Headless core</small></span><b>→</b><strong
          >RunicFlow.ApplicationBridge<br /><small>Owned by Flow</small></strong
        ><b>←</b><span
          >RunicToolkit.ApplicationBridge<br /><small
            >Application boundary</small
          ></span
        >
      </div>
    </article>
    <article class="info-card">
      <p class="eyebrow">Product autonomy</p>
      <h2>Independent histories</h2>
      <p>
        Flow, Assets, Translations, Translations Editor, Command Line, Toolkit,
        and CsWebUi each version and release from their own repository.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Stable seams</p>
      <h2>Explicit compatibility</h2>
      <p>
        Integrations pin exact cross-product versions during the preview.
        Compatibility is evidence recorded by consumers, frontend builds, and
        applicable NativeAOT runs.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Framework neutrality</p>
      <h2>Cores stay portable</h2>
      <p>
        Runic Flow owns no UI state. Runic Assets owns no host. Runic
        Translations begins with .NET but defines language-neutral contracts.
      </p>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Application boundary</p>
      <h2>The editor consumes the translation system</h2>
      <p>
        Runic Translations owns schemas, compiler behavior, runtime contracts,
        generators, and authoring APIs. Runic Translations Editor consumes those
        surfaces as a downstream desktop application and owns translator UX,
        packaging, and releases.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Integration ownership</p>
      <h2>Behavior lives with its author</h2>
      <p>
        <code>RunicAssets.RunicToolkit</code> and
        <code>RunicFlow.ApplicationBridge</code> live and release with Assets and
        Flow respectively.
      </p>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Application boundary</p>
      <h2>Schema first, renderer last</h2>
      <p>
        Effect Schema is the authority for Application Bridge wire values.
        Deterministic JSON Schema and a canonical manifest feed the
        reflection-free C# generator. UI frameworks then project validated
        application events into their own state systems without owning transport
        lifecycle.
      </p>
    </article>
  </section>
</main>
