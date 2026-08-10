<script lang="ts">
  import { resolve } from '$app/paths';
  import { products } from '$lib/docs-data';

  const frontendIntegrations = [
    {
      name: '@runic-artifex/svelte',
      owner: 'runic-svelte',
      source: 'https://github.com/Runic-Artifex/runic-svelte',
    },
    {
      name: '@runic-artifex/sveltekit',
      owner: 'runic-svelte',
      source: 'https://github.com/Runic-Artifex/runic-svelte',
    },
    {
      name: '@runic-artifex/vite-plugin-runic-toolkit',
      owner: 'runic-vite',
      source: 'https://github.com/Runic-Artifex/runic-vite',
    },
  ] as const;

  const rows = products.flatMap((product) => [
    ...product.packages.map((name) => ({ name, registry: 'NuGet', product })),
    ...(product.npmPackages ?? []).map((name) => ({
      name,
      registry: 'npm',
      product,
    })),
  ]);
</script>

<svelte:head>
  <title>Package catalog · Runic Artifex</title>
  <meta
    name="description"
    content="The NuGet and npm packages owned by each Runic Artifex product."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Package catalog</p>
    <h1>One owner for every public package.</h1>
    <p class="lede">
      Package families release independently. Candidate refreshes are in
      progress; versions marked pending are not yet public install promises.
    </p>
  </section>
  <section class="content-grid shell">
    <div class="package-table">
      <table>
        <thead
          ><tr
            ><th>Package</th><th>Registry</th><th>Owner</th><th
              >Public status</th
            ></tr
          ></thead
        >
        <tbody>
          {#each rows as { name, registry, product } (`${registry}:${name}`)}
            <tr>
              <td>{name}</td><td>{registry}</td><td
                ><a href={resolve('/products/[slug]', { slug: product.slug })}
                  >{product.name}</a
                ></td
              ><td><code>{product.version}</code></td>
            </tr>
          {/each}
          {#each frontendIntegrations as integration (integration.name)}
            <tr>
              <td>{integration.name}</td><td>npm</td><td>{integration.owner}</td
              ><td><code>Candidate refresh pending</code></td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    <article class="info-card full">
      <p class="eyebrow">Canonical names</p>
      <h2>Product and package identifiers agree</h2>
      <p>
        Runic Translations is the product and repository name. <code
          >RunicTranslations.*</code
        >,
        <code>runic.translations/1</code>, and
        <code>@runic-artifex/vite-plugin-runic-translations</code> are the canonical
        identifiers.
      </p>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Version policy</p>
      <h2>Exact during preview, explicit afterward</h2>
      <p>
        Each repository releases one version across its owned package family.
        Cross-product dependencies remain exact while contracts settle.
        Applications decide their own update cadence.
      </p>
    </article>
  </section>
</main>
