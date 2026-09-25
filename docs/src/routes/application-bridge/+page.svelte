<script lang="ts">
  import { resolve } from '$app/paths';
</script>

<svelte:head>
  <title>Application guidance moved · Runic Artifex</title>
  <meta http-equiv="refresh" content="0;url=/views" />
</svelte:head>

<div class="page-hero shell">
  <p class="eyebrow">Runic Application</p>
  <h1>Application guidance has moved.</h1>
  <p class="lede">
    The current application model uses explicit .NET Windows and Views with
    generated TypeScript clients.
  </p>
  <a class="text-link" href={resolve('/views')}>Read the Windows and Views guide</a>
</div>
