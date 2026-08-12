<script lang="ts">
  import { resolve } from '$app/paths';
  import { page } from '$app/state';
  import {
    applyAppearance,
    readAppearance,
    saveAppearance,
    subscribeSystemAppearance,
    type ThemeMode,
    type ThemePalette,
  } from '$lib/appearance';
  import AppearanceMenu from '$lib/components/AppearanceMenu.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Separator } from '$lib/components/ui/separator';
  import * as Sheet from '$lib/components/ui/sheet';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import CodeXmlIcon from '@lucide/svelte/icons/code-xml';
  import MenuIcon from '@lucide/svelte/icons/menu';
  import { onMount, type Snippet } from 'svelte';
  import './layout.css';
  import '../app.css';

  let { children }: { children: Snippet } = $props();
  let mobileOpen = $state(false);
  let themeMode = $state<ThemeMode>('dark');
  let themePalette = $state<ThemePalette>('runic');

  const navigation = [
    { href: '/getting-started', label: 'Start' },
    { href: '/products', label: 'Products' },
    { href: '/application-bridge', label: 'Application Bridge' },
    { href: '/architecture', label: 'Architecture' },
    { href: '/packages', label: 'Packages' },
    { href: '/releases', label: 'Releases' },
  ] as const;

  function isCurrent(href: (typeof navigation)[number]['href']) {
    return href === '/products'
      ? page.url.pathname.startsWith('/products')
      : page.url.pathname.startsWith(href);
  }

  function changeThemeMode(mode: ThemeMode): void {
    themeMode = mode;
    saveAppearance(themeMode, themePalette);
  }

  function changeThemePalette(palette: ThemePalette): void {
    themePalette = palette;
    saveAppearance(themeMode, themePalette);
  }

  onMount(() => {
    const appearance = readAppearance();
    themeMode = appearance.mode;
    themePalette = appearance.palette;
    applyAppearance(themeMode, themePalette);
    return subscribeSystemAppearance(() => ({
      mode: themeMode,
      palette: themePalette,
    }));
  });
</script>

<svelte:head>
  <link rel="icon" href="/icon.png" />
  <link rel="canonical" href={`${page.url.origin}${page.url.pathname}`} />
  <meta property="og:type" content="website" />
  <meta property="og:url" content={`${page.url.origin}${page.url.pathname}`} />
  <meta property="og:site_name" content="Runic Artifex Documentation" />
  <meta property="og:image" content={`${page.url.origin}/og.png`} />
  <meta property="og:image:width" content="1200" />
  <meta property="og:image:height" content="630" />
  <meta property="og:image:alt" content="Runic Artifex documentation" />
  <meta name="twitter:card" content="summary_large_image" />
  <meta name="twitter:image" content={`${page.url.origin}/og.png`} />
</svelte:head>

<Tooltip.Provider>
  <a class="skip-link" href="#content">Skip to content</a>
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

      <nav class="desktop-nav" aria-label="Primary navigation">
        {#each navigation as item (item.href)}
          <a
            href={resolve(item.href)}
            aria-current={isCurrent(item.href) ? 'page' : undefined}
            >{item.label}</a
          >
        {/each}
      </nav>

      <noscript>
        <details class="noscript-nav">
          <summary>Navigation</summary>
          <nav aria-label="Mobile navigation without JavaScript">
            {#each navigation as item (item.href)}
              <a
                href={resolve(item.href)}
                aria-current={isCurrent(item.href) ? 'page' : undefined}
                >{item.label}</a
              >
            {/each}
          </nav>
        </details>
      </noscript>

      <div class="flex items-center gap-2">
        <div class="desktop-appearance">
          <AppearanceMenu
            mode={themeMode}
            palette={themePalette}
            compact
            onmodechange={changeThemeMode}
            onpalettechange={changeThemePalette}
          />
        </div>
        <Tooltip.Root>
          <Tooltip.Trigger>
            {#snippet child({ props })}
              <Button
                {...props}
                class="hidden sm:inline-flex"
                href="https://github.com/Runic-Artifex"
                variant="ghost"
                size="icon"
                aria-label="Runic Artifex on GitHub"
              >
                <CodeXmlIcon />
              </Button>
            {/snippet}
          </Tooltip.Trigger>
          <Tooltip.Content>GitHub organization</Tooltip.Content>
        </Tooltip.Root>

        <Sheet.Root bind:open={mobileOpen}>
          <Sheet.Trigger>
            {#snippet child({ props })}
              <Button
                {...props}
                class="mobile-menu-button"
                variant="outline"
                size="icon"
                aria-label="Open documentation navigation"
              >
                <MenuIcon />
              </Button>
            {/snippet}
          </Sheet.Trigger>
          <Sheet.Content side="right" class="w-[min(88vw,24rem)]">
            <Sheet.Header>
              <Sheet.Title class="font-serif text-2xl"
                >Documentation</Sheet.Title
              >
              <Sheet.Description>
                Explore Runic Artifex products, architecture, packages, and
                release readiness.
              </Sheet.Description>
            </Sheet.Header>
            <Separator />
            <nav class="mobile-nav" aria-label="Mobile navigation">
              {#each navigation as item (item.href)}
                <a
                  href={resolve(item.href)}
                  aria-current={isCurrent(item.href) ? 'page' : undefined}
                  onclick={() => (mobileOpen = false)}>{item.label}</a
                >
              {/each}
            </nav>
            <Separator />
            <div class="grid gap-3 p-5">
              <AppearanceMenu
                mode={themeMode}
                palette={themePalette}
                onmodechange={changeThemeMode}
                onpalettechange={changeThemePalette}
              />
              <Button
                href="https://runic-artifex.eu/"
                variant="outline"
                class="w-full"
              >
                Runic Artifex website
              </Button>
              <Button
                href="https://github.com/Runic-Artifex"
                variant="outline"
                class="w-full"
              >
                <CodeXmlIcon />
                GitHub organization
              </Button>
            </div>
          </Sheet.Content>
        </Sheet.Root>
      </div>
    </div>
  </header>

  <main id="content" tabindex="-1">{@render children()}</main>

  <Separator />
  <footer class="site-footer">
    <div class="shell footer-grid">
      <div>
        <a
          class="brand footer-brand"
          href="https://runic-artifex.eu/"
          aria-label="Visit the Runic Artifex project website"
        >
          <span class="brand-mark" aria-hidden="true"></span>
          <span
            ><strong>Runic Artifex</strong><small
              >The map of independent tools and explicit seams.</small
            ></span
          >
        </a>
        <p>NativeAOT-minded building blocks for modern .NET applications.</p>
      </div>
      <div>
        <strong>Explore</strong><a href={resolve('/products')}>Products</a><a
          href={resolve('/packages')}>Package catalog</a
        ><a href={resolve('/architecture')}>Architecture</a>
      </div>
      <div>
        <strong>Project</strong><a href="https://runic-artifex.eu/"
          >Runic Artifex website</a
        ><a href="https://github.com/Runic-Artifex">GitHub organization</a><a
          href={resolve('/releases')}>Release status</a
        ><a
          href="https://github.com/Runic-Artifex/.github/blob/main/SECURITY.md"
          >Security</a
        >
      </div>
    </div>
    <Separator class="shell" />
    <div class="shell footer-bottom">
      <span>© 2026 Runic Artifex</span><span
        >MIT where possible · Preview documentation</span
      >
    </div>
  </footer>
</Tooltip.Provider>
