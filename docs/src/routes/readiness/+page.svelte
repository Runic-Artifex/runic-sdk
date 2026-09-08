<script lang="ts">
  import { releaseSummary } from '$lib/release-docs';
</script>

<svelte:head>
  <title>Release readiness · Runic Artifex</title>
  <meta
    name="description"
    content="Required verification and remaining coverage for the Runic SDK preview."
  />
</svelte:head>
<div class="shell content-page">
  <p class="eyebrow">Release readiness</p>
  <h1>Verify the candidate before publishing.</h1>
  <p class="lede">{releaseSummary}</p>
  <section>
    <h2>Required evidence</h2>
    <p>
      Full CI covers managed behavior, browser flows, package consumers,
      templates and native JIT/NativeAOT on Linux x64, Windows x64 and macOS
      arm64. Manual checks use this Linux system and the available Windows VM.
      Review, matched performance measurements and a two-hour soak remain
      required.
    </p>
    <p>
      Every acceptance receipt identifies the source commit, exact artifact
      hashes, environment, scenario and outcome. A simulated picker does not
      prove selected-file permissions, and a source build does not prove public
      registry installation.
    </p>
  </section>
  <section>
    <h2>Coverage before v1</h2>
    <p>
      Real macOS and signed sandbox checks, unavailable Wayland profiles,
      broader accessibility certification and independent developer pilots are
      deferred. No completed native certification is implied for these profiles.
    </p>
  </section>
  <a
    class="text-link"
    href="https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/0.2.0-preview.1.md"
    >Preview scope and installation guide</a
  >
</div>
