<script lang="ts">
  import { resolve } from '$app/paths';
  import CommandBlock from '#lib/components/CommandBlock.svelte';
  import * as Card from '#lib/components/ui/card/index.js';
  import {
    choiceFor,
    creatorCommands,
    creatorOptions,
    defaultSelection,
    isValidProjectName,
  } from '#lib/creator.js';
  import { currentRelease } from '#lib/release-docs.js';
  import { previewFiles } from '#lib/template-preview.js';
  import { onMount } from 'svelte';

  const version = currentRelease.version;

  // Prerequisites that depend on a choice. Every project needs the .NET 10 SDK.
  const requirements: Readonly<Record<string, { text: string; href: string }>> =
    {
      npm: {
        text: 'Node.js 24 with npm',
        href: 'https://nodejs.org/en/download',
      },
      pnpm: {
        text: 'Node.js 24 with pnpm 12',
        href: 'https://pnpm.io/installation',
      },
      bun: { text: 'Bun 1.4 or later', href: 'https://bun.sh' },
      cswebui: {
        text: 'A Chromium-based browser, or the platform WebView as a fallback',
        href: 'https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/desktop/host-selection.md',
      },
      desktop: {
        text: 'WebView2 on Windows, WKWebView on macOS, or GTK 3 with WebKitGTK 4.1 on Linux',
        href: 'https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md',
      },
    };

  let name = $state('MyApp');
  let selection = $state<Record<string, string>>(defaultSelection());
  let mode = $state<'creator' | 'template'>('creator');
  let activeFile = $state(0);
  let ready = $state(false);

  let validName = $derived(isValidProjectName(name));
  let projectName = $derived(validName ? name : 'MyApp');
  let commands = $derived(creatorCommands(version, projectName, selection));
  let files = $derived(previewFiles(projectName, version, selection));
  let file = $derived(files[Math.min(activeFile, files.length - 1)]);
  let needs = $derived(
    creatorOptions
      .map((option) => requirements[choiceFor(option, selection).value])
      .filter((entry) => entry !== undefined),
  );

  // Shareable links: ?frontend=svelte&host=desktop selects those choices.
  onMount(() => {
    const query = new URLSearchParams(window.location.search);
    const requestedName = query.get('name');
    if (requestedName && isValidProjectName(requestedName))
      name = requestedName;
    for (const option of creatorOptions) {
      const value = query.get(option.flag.slice(2));
      if (value && option.choices.some((choice) => choice.value === value))
        selection[option.symbol] = value;
    }
    ready = true;
  });

  $effect(() => {
    if (!ready) return;
    const query: [string, string][] = [];
    if (projectName !== 'MyApp') query.push(['name', projectName]);
    for (const option of creatorOptions) {
      const value = choiceFor(option, selection).value;
      if (value !== option.defaultValue)
        query.push([option.flag.slice(2), value]);
    }
    const search =
      query.length > 0
        ? `?${query.map(([key, value]) => `${key}=${encodeURIComponent(value)}`).join('&')}`
        : '';
    if (search !== window.location.search)
      history.replaceState(
        history.state,
        '',
        `${window.location.pathname}${search}`,
      );
  });

  function selectFile(event: KeyboardEvent) {
    const step =
      event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (step === 0) return;
    event.preventDefault();
    activeFile = (activeFile + step + files.length) % files.length;
    document.getElementById(`preview-tab-${activeFile}`)?.focus();
  }
</script>

<svelte:head>
  <title>Create an app · Runic Artifex</title>
  <meta
    name="description"
    content="Choose a frontend, package manager, Window host and ViewModel library, then copy one command that creates your Runic application."
  />
  <meta property="og:title" content="Create an app · Runic Artifex" />
  <meta
    property="og:description"
    content="Choose your stack and copy one command that creates your Runic application."
  />
  <meta name="twitter:title" content="Create an app · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Choose your stack and copy one command that creates your Runic application."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Create</p>
    <h1>Put your app together.</h1>
    <p class="lede">
      Choose a frontend, package manager, Window host, and ViewModel library.
      The command below creates exactly this project. It makes the same choices <code
        >dnx Runic.Create</code
      > asks for in your terminal.
    </p>
  </section>

  <section class="creator shell" aria-label="Project creator">
    <form class="creator-options" onsubmit={(event) => event.preventDefault()}>
      <label class="creator-field">
        <span class="creator-legend">Project name</span>
        <input
          class="creator-input"
          bind:value={name}
          autocomplete="off"
          spellcheck="false"
          aria-invalid={!validName}
          aria-describedby="project-name-help"
        />
        <span
          id="project-name-help"
          class={validName ? 'creator-help' : 'creator-help creator-error'}
        >
          {validName
            ? 'Also the folder, C# namespace, and frontend package name.'
            : "Start with a letter and use letters, digits, '.', '_' or '-'."}
        </span>
      </label>

      {#each creatorOptions as option (option.symbol)}
        <fieldset class="creator-field">
          <legend class="creator-legend">{option.label}</legend>
          <p class="creator-help">{option.description}</p>
          <div class="choice-grid">
            {#each option.choices as choice (choice.value)}
              <label class="choice-card">
                <input
                  type="radio"
                  name={option.symbol}
                  value={choice.value}
                  bind:group={selection[option.symbol]}
                />
                <span class="choice-label"
                  >{choice.label}{#if choice.value === option.defaultValue}<small
                      >default</small
                    >{/if}</span
                >
                <span class="choice-description">{choice.description}</span>
              </label>
            {/each}
          </div>
        </fieldset>
      {/each}
    </form>

    <div class="creator-output">
      <Card.Root class="creator-card">
        <Card.Header>
          <p class="eyebrow">SDK {version}</p>
          <Card.Title
            ><h2 class="creator-card-title">Your command</h2></Card.Title
          >
        </Card.Header>
        <Card.Content class="grid gap-4">
          <div class="mode-tabs" role="radiogroup" aria-label="Command style">
            <label
              ><input
                type="radio"
                bind:group={mode}
                value="creator"
              />Runic.Create</label
            >
            <label
              ><input type="radio" bind:group={mode} value="template" />dotnet
              new</label
            >
          </div>
          {#if mode === 'creator'}
            <CommandBlock command={commands.creator} />
            <p class="creator-help">
              <code>dnx</code> runs the creator without installing it. It
              installs
              <code>Runic.Application.Templates</code> and runs
              <code>dotnet new runic-app</code>, then prints these commands
              again. Run <code>{commands.interactive}</code> to answer the questions
              in your terminal instead.
            </p>
          {:else}
            <CommandBlock command={`${commands.install}\n${commands.create}`} />
            <p class="creator-help">
              The template's own options. <code
                >dotnet new runic-app --help</code
              >
              lists them.
            </p>
          {/if}
          <div class="grid gap-2">
            <span class="creator-legend">Then run it</span>
            <CommandBlock
              command={commands.nextSteps.join('\n')}
              label="Copy next steps"
            />
          </div>
          <div class="grid gap-2">
            <span class="creator-legend">You need</span>
            <ul class="creator-needs">
              <li>
                <a href="https://dotnet.microsoft.com/download/dotnet/10.0"
                  >The .NET 10 SDK</a
                >
              </li>
              {#each needs as need (need.text)}
                <li><a href={need.href} rel="external">{need.text}</a></li>
              {/each}
            </ul>
          </div>
        </Card.Content>
      </Card.Root>

      <Card.Root class="creator-card">
        <Card.Header>
          <Card.Title><h2 class="creator-card-title">Preview</h2></Card.Title>
          <Card.Description
            >Selected files from the template, rendered for your choices.</Card.Description
          >
        </Card.Header>
        <Card.Content class="grid gap-3">
          <div class="file-tabs" role="tablist" aria-label="Generated files">
            {#each files as entry, index (entry.path)}
              <button
                id={`preview-tab-${index}`}
                type="button"
                role="tab"
                aria-selected={index === activeFile}
                aria-controls="preview-panel"
                tabindex={index === activeFile ? 0 : -1}
                onclick={() => (activeFile = index)}
                onkeydown={selectFile}>{entry.path}</button
              >
            {/each}
          </div>
          <div
            id="preview-panel"
            class="file-preview"
            role="tabpanel"
            aria-labelledby={`preview-tab-${activeFile}`}
          >
            <pre data-language={file.language}><code>{file.content}</code></pre>
          </div>
          <p class="creator-help">
            New to the Window and View model? Read
            <a href={resolve('/views')}>Window and View</a> or the
            <a href={resolve('/getting-started')}>getting-started guide</a>.
          </p>
        </Card.Content>
      </Card.Root>
    </div>
  </section>
</div>

<style>
  .creator {
    display: grid;
    grid-template-columns: minmax(0, 0.9fr) minmax(0, 1.1fr);
    gap: 28px;
    align-items: start;
    padding-bottom: 90px;
  }

  .creator-options {
    display: grid;
    gap: 26px;
  }

  .creator-field {
    display: grid;
    gap: 8px;
    margin: 0;
    padding: 0;
    border: 0;
  }

  .creator-legend {
    padding: 0;
    color: var(--foreground);
    font-size: 0.78rem;
    font-weight: 700;
    letter-spacing: 0.12em;
    text-transform: uppercase;
  }

  .creator-help {
    color: var(--muted-foreground);
    font-size: 0.92rem;
  }

  .creator-error {
    color: var(--destructive);
  }

  .creator-input {
    height: 42px;
    padding-inline: 14px;
    border: 1px solid var(--border);
    border-radius: calc(var(--radius) * 1.4);
    background: var(--card);
    color: var(--foreground);
    font:
      500 1rem 'Geist Mono',
      monospace;
  }

  .creator-input:focus-visible {
    outline: 2px solid var(--ring);
    outline-offset: 2px;
  }

  .creator-input[aria-invalid='true'] {
    border-color: var(--destructive);
  }

  .choice-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
    gap: 10px;
  }

  .choice-card {
    position: relative;
    display: grid;
    gap: 4px;
    padding: 14px 16px;
    border: 1px solid var(--border);
    border-radius: calc(var(--radius) * 1.6);
    background: var(--card);
    cursor: pointer;
    transition:
      border-color 120ms,
      background-color 120ms;
  }

  .choice-card:hover {
    border-color: color-mix(in oklch, var(--primary) 45%, var(--border));
  }

  .choice-card:has(input:checked) {
    border-color: var(--primary);
    background: color-mix(in oklch, var(--primary) 12%, var(--card));
  }

  .choice-card:has(input:focus-visible) {
    outline: 2px solid var(--ring);
    outline-offset: 2px;
  }

  .choice-card input {
    position: absolute;
    opacity: 0;
    pointer-events: none;
  }

  .choice-label {
    display: flex;
    align-items: baseline;
    gap: 8px;
    font-weight: 650;
  }

  .choice-label small {
    color: var(--muted-foreground);
    font-size: 0.7rem;
    font-weight: 600;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  .choice-description {
    color: var(--muted-foreground);
    font-size: 0.86rem;
    line-height: 1.45;
  }

  .creator-output {
    position: sticky;
    top: 88px;
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 16px;
  }

  /* Long commands scroll inside their blocks instead of widening the column. */
  .creator-output :global(.grid) {
    grid-template-columns: minmax(0, 1fr);
  }

  .creator-card-title {
    font-size: 1.7rem;
  }

  .mode-tabs {
    display: inline-flex;
    justify-self: start;
    gap: 4px;
    padding: 4px;
    border: 1px solid var(--border);
    border-radius: 999px;
  }

  .mode-tabs label {
    padding: 4px 14px;
    border-radius: 999px;
    color: var(--muted-foreground);
    font:
      600 0.85rem 'Geist Mono',
      monospace;
    cursor: pointer;
  }

  .mode-tabs label:has(input:checked) {
    background: var(--secondary);
    color: var(--secondary-foreground);
  }

  .mode-tabs label:has(input:focus-visible) {
    outline: 2px solid var(--ring);
  }

  .mode-tabs input {
    position: absolute;
    opacity: 0;
    pointer-events: none;
  }

  .creator-needs {
    display: grid;
    gap: 4px;
    margin: 0;
    padding-left: 1.2rem;
    color: var(--muted-foreground);
    font-size: 0.92rem;
  }

  .file-tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }

  .file-tabs button {
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: transparent;
    color: var(--muted-foreground);
    font:
      500 0.8rem 'Geist Mono',
      monospace;
    cursor: pointer;
  }

  .file-tabs button[aria-selected='true'] {
    border-color: var(--primary);
    color: var(--foreground);
  }

  .file-preview pre {
    max-height: 460px;
    margin: 0;
    padding: 16px 18px;
    overflow: auto;
    border: 1px solid var(--border);
    border-radius: calc(var(--radius) * 1.4);
    background: color-mix(in oklch, var(--muted) 65%, var(--background));
    font-size: 0.8rem;
    line-height: 1.6;
  }

  @media (max-width: 960px) {
    .creator {
      grid-template-columns: 1fr;
    }

    .creator-output {
      position: static;
    }
  }
</style>
