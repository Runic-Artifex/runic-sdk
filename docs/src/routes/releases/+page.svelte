<script lang="ts">
  const train = [
    ['CsWebUi', '2.5.0-beta.4.4', 'Available on nuget.org'],
    ['Runic Command Line', 'Candidate review required', 'Independent'],
    [
      'Runic Translations',
      'Candidate refresh required',
      'Independent; NuGet and npm paths',
    ],
    [
      'Runic Translations Editor',
      'First preview pending',
      'After Runic Translations',
    ],
    [
      'Runic Toolkit',
      'Candidate refresh required',
      'Before Toolkit integrations',
    ],
    [
      'Runic Svelte integrations',
      'Candidate refresh required',
      'After Runic Toolkit',
    ],
    [
      'Runic Vite integration',
      'Candidate refresh required',
      'After Runic Toolkit',
    ],
    ['Runic Assets', 'Candidate refresh required', 'After Runic Toolkit'],
    [
      'Runic Flow',
      'First headless candidate required',
      'After Toolkit and downstream migration',
    ],
  ] as const;
</script>

<svelte:head>
  <title>Releases · Runic Artifex</title>
  <meta
    name="description"
    content="Current public preview readiness and registry safety model for Runic Artifex."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Release policy</p>
    <h1>Verify once. Publish the same artifacts.</h1>
    <p class="lede">
      Source repositories may become public before packages. Registry jobs
      remain gated on clean candidates, package consumption, metadata
      validation, digest recording, and accurate docs.
    </p>
  </section>
  <section class="content-grid shell">
    <div class="release-steps">
      <article class="release-step">
        <div>
          <h3>Freeze exact source commits</h3>
          <p>
            Finish product changes and select immutable release inputs from
            green main branches.
          </p>
          <span class="status-pill">In progress</span>
        </div>
      </article>
      <article class="release-step">
        <div>
          <h3>Build fresh candidates</h3>
          <p>
            Restore, test, pack, and run frontend, downstream-consumer, and
            applicable NativeAOT gates without sibling source dependencies.
          </p>
          <span class="status-pill">Required</span>
        </div>
      </article>
      <article class="release-step">
        <div>
          <h3>Cross the documentation gate</h3>
          <p>
            Review the portal against those exact artifacts and remove stale
            product or version claims.
          </p>
          <span class="status-pill">In progress</span>
        </div>
      </article>
      <article class="release-step">
        <div>
          <h3>Enable trusted publishing</h3>
          <p>
            Use protected environments and OIDC for NuGet and npm. Use a
            short-lived bootstrap token only where npm requires a first package
            record.
          </p>
          <span class="status-pill">Launch setup</span>
        </div>
      </article>
    </div>
    <div class="package-table">
      <table>
        <thead
          ><tr
            ><th>Product</th><th>Public status</th><th>Order constraint</th></tr
          ></thead
        >
        <tbody>
          {#each train as [name, status, constraint] (name)}
            <tr><td>{name}</td><td>{status}</td><td>{constraint}</td></tr>
          {/each}
        </tbody>
      </table>
    </div>
  </section>
</main>
