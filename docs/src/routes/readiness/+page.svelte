<script lang="ts">
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Release process · Runic Artifex</title>
  <meta
    name="description"
    content="Where to find SDK release notes and contributor release instructions."
  />
</svelte:head>
<div class="shell content-page">
  <p class="eyebrow">Release process</p>
  <h1>Release information has moved.</h1>
  <p class="lede">
    For published versions and upgrade guidance, visit <a
      href={resolve('/releases')}>Releases</a
    >.
  </p>
  <p>
    Maintainers use the <a
      href="https://github.com/Runic-Artifex/runic-sdk/blob/main/eng/release/README.md"
      >SDK release guide</a
    >. The former readiness checklist is retired.
  </p>
</div>
