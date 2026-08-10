<script lang="ts">
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Getting started · Runic Artifex</title>
  <meta
    name="description"
    content="Choose a Runic Artifex product and prepare for the first public preview."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Getting started</p>
    <h1>Begin at the capability boundary.</h1>
    <p class="lede">
      Runic Artifex is not one mandatory stack. Choose an independent product,
      then add integrations owned by that product when your application needs
      them.
    </p>
  </section>
  <section class="content-grid shell">
    <article class="info-card">
      <p class="eyebrow">01 · Choose</p>
      <h2>Pick the owner</h2>
      <ul>
        <li>
          Need a desktop browser host? Start with <a
            href={resolve('/products/[slug]', { slug: 'cs-webui' })}>CsWebUi</a
          >.
        </li>
        <li>
          Need application composition? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-toolkit' })}
            >Runic Toolkit</a
          >.
        </li>
        <li>
          Need deterministic backend operations? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-flow' })}
            >Runic Flow</a
          >.
        </li>
        <li>
          Need localization contracts and builds? Start with <a
            href={resolve('/products/[slug]', { slug: 'runic-translations' })}
            >Runic Translations</a
          >.
        </li>
        <li>
          Need a translator-facing workspace? Use <a
            href={resolve('/products/[slug]', {
              slug: 'runic-translations-editor',
            })}>Runic Translations Editor</a
          >.
        </li>
      </ul>
    </article>
    <article class="info-card">
      <p class="eyebrow">02 · Inspect</p>
      <h2>Use the candidate status, not an old version list</h2>
      <p>
        CsWebUi is already public. Other package families are refreshing
        candidates from their final source commits. The catalog names package
        ownership now and will show installable versions only after matching
        public artifacts are ready.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">03 · Integrate</p>
      <h2>Let adapters point inward</h2>
      <p>
        Runic Flow remains headless and frontend-neutral. <code
          >RunicFlow.ApplicationBridge</code
        >
        depends on Flow and Toolkit Application Bridge and is released by Flow.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">04 · Verify</p>
      <h2>Keep the application in control</h2>
      <ul>
        <li>Pin exact product versions during the preview.</li>
        <li>
          Publish the final application with trimming and NativeAOT when
          supported.
        </li>
        <li>
          Do not add NuGet <code>packages.lock.json</code>; application package
          managers own resolution.
        </li>
        <li>
          Keep ordinary npm <code>package-lock.json</code> files for applications.
        </li>
      </ul>
    </article>
    <article class="info-card full">
      <div class="notice">
        <strong>Public launch is gated</strong>
        <p>
          Source visibility can precede registry publication. Package commands
          will be published only after fresh candidates, downstream consumers,
          trusted publishers, and the documentation review all pass.
        </p>
      </div>
      <p>
        Continue with the <a
          class="text-link"
          href={resolve('/application-bridge')}>Application Bridge guide</a
        >, the
        <a class="text-link" href={resolve('/packages')}>package catalog</a>, or
        the
        <a class="text-link" href={resolve('/architecture')}>dependency rules</a
        >.
      </p>
    </article>
  </section>
</main>
