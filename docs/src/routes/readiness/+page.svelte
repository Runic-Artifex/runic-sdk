<script lang="ts">
  import claims from '$lib/local-readiness-claims.json';
</script>

<svelte:head>
  <title>Local readiness evidence · Runic Artifex</title>
  <meta
    name="description"
    content="Local, unsigned, non-public documentation and support boundaries for the retained Runic readiness train."
  />
</svelte:head>

<div class="shell content-page">
  <p class="eyebrow">Local readiness evidence</p>
  <h1>Historical W80 evidence, not the expanded v1 verdict.</h1>
  <p class="lede">
    W80 retains the locally verified C# host, local Application Bridge, Runic
    Translations Editor desktop, and D008 hosted-product paths. The expanded
    W110 index remains pending until W105 closure and exact native-platform
    evidence are complete. Neither record assigns versions, publishes artifacts,
    or promises an updater.
  </p>

  <section aria-labelledby="retained-boundary">
    <h2 id="retained-boundary">Retained boundary</h2>
    <p>
      Evidence is local and unsigned. The hosted path retains opaque-cookie SSR
      ownership; the Bridge remains local; and localization remains the closed
      MF2-subset interchange rather than a general message or XLIFF processor.
    </p>
  </section>

  {#each claims.claims as claim (claim.id)}
    <section id={claim.anchor} aria-labelledby={`${claim.anchor}-title`}>
      <h2 id={`${claim.anchor}-title`}>{claim.id.replaceAll('-', ' ')}</h2>
      <p>{claim.text}</p>
    </section>
  {/each}

  <section aria-labelledby="nonclaims">
    <h2 id="nonclaims">Explicit nonclaims</h2>
    <p>
      No version assignment, signing, notarization, upload, publication,
      deployment, telemetry, support portal, update path, browser-success, or
      native-platform-success claim is made here.
    </p>
  </section>
</div>
