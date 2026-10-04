<script lang="ts">
  import { resolve } from '$app/paths';
  import CommandBlock from '#lib/components/CommandBlock.svelte';
  import { Button } from '#lib/components/ui/button/index.js';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.js';
  import { creatorCommands, creatorOptions } from '#lib/creator.js';
  import ArrowRightIcon from '@lucide/svelte/icons/arrow-right';
  import ChevronDownIcon from '@lucide/svelte/icons/chevron-down';

  let { version }: { version: string } = $props();

  const frontend = creatorOptions.find(
    (option) => option.flag === '--frontend',
  )!;
  const anyFrontend = 'any';
  let selected = $state(anyFrontend);
  let label = $derived(
    frontend.choices.find((choice) => choice.value === selected)?.label ??
      'Any frontend',
  );
  // Without a frontend the creator asks every question; with one it asks the rest.
  let command = $derived(
    selected === anyFrontend
      ? creatorCommands(version, 'MyApp', {}).interactive
      : `${creatorCommands(version, 'MyApp', {}).interactive} -- MyApp --frontend ${selected}`,
  );
  const worksWith = [
    ...frontend.choices.map((choice) => choice.label),
    'npm',
    'pnpm',
    'Bun',
    'Windows',
    'macOS',
    'Linux',
  ];
</script>

<section class="creator-hero shell">
  <a class="announcement" href={resolve('/create')}
    >// SDK {version}: guided creator <ArrowRightIcon aria-hidden="true" /></a
  >
  <h1>C# desktop apps with the web frontend you know.</h1>
  <p class="lede">
    Write application logic in C# and render it with React, Vue, Svelte, or
    Angular. Runic Artifex adds desktop hosting, assets, translations, and
    command-line tools when your app needs them.
  </p>
  <CommandBlock class="hero-command" {command}>
    {#snippet leading()}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger>
          {#snippet child({ props })}
            <Button
              {...props}
              variant="ghost"
              size="sm"
              class="hero-frontend"
              aria-label={`Frontend: ${label}`}
            >
              {label}
              <ChevronDownIcon aria-hidden="true" />
            </Button>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="start" class="min-w-44">
          <DropdownMenu.Label>Frontend</DropdownMenu.Label>
          <DropdownMenu.RadioGroup bind:value={selected}>
            <DropdownMenu.RadioItem value={anyFrontend}
              >Any frontend</DropdownMenu.RadioItem
            >
            {#each frontend.choices as choice (choice.value)}
              <DropdownMenu.RadioItem value={choice.value}
                >{choice.label}</DropdownMenu.RadioItem
              >
            {/each}
          </DropdownMenu.RadioGroup>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    {/snippet}
  </CommandBlock>
  <p class="hero-note">
    Needs the .NET 10 SDK. <a href={resolve('/create')}
      >Choose every option in the browser</a
    >
    or read the <a href={resolve('/getting-started')}>getting-started guide</a>.
  </p>
  <p class="works-with-title">// Works with</p>
  <ul
    class="works-with"
    aria-label="Supported frontends, package managers, and platforms"
  >
    {#each worksWith as item (item)}<li>{item}</li>{/each}
  </ul>
</section>

<style>
  .creator-hero {
    display: grid;
    justify-items: center;
    padding-block: 72px 88px;
    text-align: center;
  }

  .announcement {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    margin-bottom: 26px;
    color: var(--muted-foreground);
    font:
      500 0.82rem 'Geist Mono',
      monospace;
    letter-spacing: 0.08em;
    text-decoration: none;
    text-transform: uppercase;
  }

  .announcement:hover,
  .announcement:focus-visible {
    color: var(--docs-accent);
  }

  .announcement :global(svg) {
    width: 0.95rem;
    height: 0.95rem;
  }

  .creator-hero > :global(h1) {
    max-width: 15ch;
    font-size: clamp(2.8rem, 6.4vw, 5.6rem);
  }

  .lede {
    max-width: 680px;
  }

  .creator-hero :global(.hero-command) {
    width: min(100%, 620px);
    margin-top: 36px;
    text-align: left;
  }

  .creator-hero :global(.hero-frontend) {
    font:
      500 0.8rem 'Geist Mono',
      monospace;
  }

  .hero-note {
    margin-top: 14px;
    color: var(--muted-foreground);
    font-size: 0.92rem;
  }

  .works-with-title {
    margin-top: 56px;
    color: var(--muted-foreground);
    font:
      500 0.78rem 'Geist Mono',
      monospace;
    letter-spacing: 0.14em;
    text-transform: uppercase;
  }

  .works-with {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 10px 26px;
    max-width: 760px;
    margin: 18px 0 0;
    padding: 0;
    list-style: none;
    color: var(--foreground);
    font:
      600 1.05rem 'Geist',
      sans-serif;
    opacity: 0.82;
  }
</style>
