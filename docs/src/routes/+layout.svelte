<script lang="ts">
  import { resolve } from '$app/paths';
  import { page } from '$app/state';
  import type { Snippet } from 'svelte';
  import '../app.css';

  let { children }: { children: Snippet } = $props();

  const description =
    'Documentation for the independent Runic Artifex products and their explicit integration boundaries.';
</script>

<svelte:head>
  <link rel="icon" href="/icon.png" />
  <link rel="canonical" href={`${page.url.origin}${page.url.pathname}`} />
  <meta property="og:type" content="website" />
  <meta property="og:url" content={`${page.url.origin}${page.url.pathname}`} />
  <meta property="og:site_name" content="Runic Artifex Documentation" />
  <meta property="og:title" content="Runic Artifex Documentation" />
  <meta property="og:description" content={description} />
  <meta property="og:image" content={`${page.url.origin}/og.png`} />
  <meta property="og:image:width" content="1200" />
  <meta property="og:image:height" content="630" />
  <meta property="og:image:alt" content="Runic Artifex documentation" />
  <meta name="twitter:card" content="summary_large_image" />
  <meta name="twitter:title" content="Runic Artifex Documentation" />
  <meta name="twitter:description" content={description} />
  <meta name="twitter:image" content={`${page.url.origin}/og.png`} />
</svelte:head>

<a class="skip-link" href={resolve(`${page.url.pathname}#content` as '/')}
  >Skip to content</a
>
<header class="site-header">
  <div class="shell header-inner">
    <a
      class="brand"
      href={resolve('/')}
      aria-label="Runic Artifex documentation home"
    >
      <span class="brand-mark" aria-hidden="true"></span>
      <span><strong>Runic Artifex</strong><small>Documentation</small></span>
    </a>
    <nav aria-label="Primary navigation">
      <a href={resolve('/getting-started')}>Start</a>
      <a href={resolve('/products')}>Products</a>
      <a href={resolve('/application-bridge')}>Application Bridge</a>
      <a href={resolve('/architecture')}>Architecture</a>
      <a href={resolve('/packages')}>Packages</a>
      <a href={resolve('/releases')}>Releases</a>
    </nav>
  </div>
</header>
<div id="content">{@render children()}</div>
<footer class="site-footer">
  <div class="shell footer-grid">
    <div>
      <div class="brand footer-brand">
        <span class="brand-mark" aria-hidden="true"></span>
        <span
          ><strong>Runic Artifex</strong><small
            >Independent tools. Explicit seams.</small
          ></span
        >
      </div>
      <p>NativeAOT-minded building blocks for modern .NET applications.</p>
    </div>
    <div>
      <strong>Explore</strong><a href={resolve('/products')}>Products</a><a
        href={resolve('/packages')}>Package catalog</a
      ><a href={resolve('/architecture')}>Architecture</a>
    </div>
    <div>
      <strong>Project</strong><a href="https://github.com/Runic-Artifex"
        >GitHub organization</a
      ><a href={resolve('/releases')}>Release policy</a><a
        href="https://github.com/Runic-Artifex/.github/blob/main/SECURITY.md"
        >Security</a
      >
    </div>
  </div>
  <div class="shell footer-bottom">
    <span>© 2026 Runic Artifex</span><span
      >MIT where possible · Preview documentation</span
    >
  </div>
</footer>
