<script lang="ts">
  import { resolve } from "$app/paths";
  import { localizeReference, m } from "$lib/messages";

  let { data }: import("./$types").PageProps = $props();
  let validationMessage = $state("");
  let pending = $state(false);
  const locale = $derived(data.locale);
  const title = $derived(m["Application.Title"]({ locale }));
  const lead = $derived(m["Application.Lead"]({ locale }));

  async function submitInvalidRegistration() {
    pending = true;
    validationMessage = "";
    try {
      const response = await fetch("/api/registration", { method: "POST" });
      validationMessage = localizeReference(await response.json(), locale);
    } catch (error) {
      validationMessage = error instanceof Error ? error.message : String(error);
    } finally {
      pending = false;
    }
  }
</script>

<svelte:head>
  <title>{title}</title>
</svelte:head>

<main>
  <header>
    <p class="eyebrow">runic.translations/1</p>
    <h1>{title}</h1>
    <p class="lead">{lead}</p>
  </header>

  <nav aria-label="Language">
    <a href={resolve("/en")} aria-current={locale === "en" ? "page" : undefined}>
      {m["Locale.English"]({ locale })}
    </a>
    <a href={resolve("/de")} aria-current={locale === "de" ? "page" : undefined}>
      {m["Locale.German"]({ locale })}
    </a>
  </nav>

  <section aria-labelledby="transport-title">
    <h2 id="transport-title">Typed backend reference</h2>
    <p>
      The .NET endpoint returns a catalog fingerprint, stable key, typed arguments, and an English
      fallback. The browser validates the envelope before formatting it in the URL locale.
    </p>
    <button type="button" onclick={submitInvalidRegistration} disabled={pending}>
      {pending ? "…" : "Request validation failure"}
    </button>
    {#if validationMessage}
      <p class="result" aria-live="polite">{validationMessage}</p>
    {/if}
  </section>

  <section aria-labelledby="contract-title">
    <h2 id="contract-title">Compatibility fixture</h2>
    <ul>
      <li>Explicit locale on every SSR message call</li>
      <li>Generated transport and dynamic-pack validation</li>
      <li>Measured production tree-shaking</li>
    </ul>
  </section>
</main>

<style>
  :global(*) {
    box-sizing: border-box;
  }

  :global(body) {
    margin: 0;
    background: #f4f1ea;
    color: #1d2824;
    font-family: Inter, ui-sans-serif, system-ui, sans-serif;
  }

  main {
    width: min(48rem, calc(100% - 2rem));
    margin: 0 auto;
    padding: 5rem 0;
  }

  header {
    margin-bottom: 2rem;
  }

  .eyebrow {
    margin: 0 0 0.5rem;
    color: #4f6d5f;
    font-family: ui-monospace, monospace;
    font-size: 0.8rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  h1 {
    max-width: 14ch;
    margin: 0;
    font-family: Georgia, serif;
    font-size: clamp(2.5rem, 8vw, 5rem);
    line-height: 0.95;
  }

  .lead {
    max-width: 38rem;
    font-size: 1.2rem;
    line-height: 1.6;
  }

  nav {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 2rem;
  }

  nav a,
  button {
    border: 1px solid #1d2824;
    border-radius: 999px;
    padding: 0.65rem 1rem;
    color: inherit;
    background: transparent;
    font: inherit;
    text-decoration: none;
  }

  nav a[aria-current="page"],
  button {
    background: #1d2824;
    color: #fff;
  }

  button {
    cursor: pointer;
  }

  button:disabled {
    cursor: wait;
    opacity: 0.65;
  }

  section {
    margin-top: 1rem;
    border: 1px solid #c5c7bd;
    border-radius: 1rem;
    background: #fffdf7;
    padding: 1.5rem;
  }

  section p,
  li {
    line-height: 1.6;
  }

  .result {
    border-left: 0.25rem solid #b4432f;
    padding-left: 1rem;
    color: #7b281d;
    font-weight: 650;
  }
</style>
