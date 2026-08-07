<script lang="ts">
  import { page } from "$app/state";
</script>

<main class="error-shell">
  <p>SvelteKit startup failed</p>
  <h1>{page.status}</h1>
  <pre data-e2e-error>{page.error?.message ?? "Unknown client error"}</pre>
</main>

<style>
  .error-shell { margin: 3rem auto; max-width: 48rem; padding: 2rem; font-family: system-ui, sans-serif; }
  pre { overflow-wrap: anywhere; white-space: pre-wrap; }
</style>
