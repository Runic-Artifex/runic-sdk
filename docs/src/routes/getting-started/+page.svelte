<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import { Button } from '$lib/components/ui/button';
  import {
    catalogRows,
    packageInstallCommand,
    currentRelease,
  } from '$lib/release-docs';
  const template = catalogRows.find(
    (entry) => entry.name === 'Runic.Application.Templates',
  )!;

  // The templates default to npm. pnpm and Bun differ only in one option, so
  // every package manager gets the same first-run sequence.
  const managers = [
    {
      id: 'npm',
      label: 'npm',
      option: '',
      prerequisite: 'Node.js 24 with npm',
      href: 'https://nodejs.org/en/download',
    },
    {
      id: 'pnpm',
      label: 'pnpm',
      option: ' --packageManager pnpm',
      prerequisite: 'Node.js 24 with pnpm 12',
      href: 'https://pnpm.io/installation',
    },
    {
      id: 'bun',
      label: 'Bun',
      option: ' --packageManager bun',
      prerequisite: 'Bun 1.4 or later',
      href: 'https://bun.sh',
    },
  ] as const;
  type ManagerId = (typeof managers)[number]['id'];
  let selected = $state<ManagerId>('npm');
  let manager = $derived(managers.find((entry) => entry.id === selected)!);
  let quickStart = $derived(`${packageInstallCommand(template)}
dotnet new runic-app-react -n MyApp${manager.option}
cd MyApp
dotnet tool restore
dotnet runic dev`);

  function selectWithKeyboard(event: KeyboardEvent) {
    const step =
      event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (step === 0) return;
    event.preventDefault();
    const index = managers.findIndex((entry) => entry.id === selected);
    const next = managers[(index + step + managers.length) % managers.length];
    selected = next.id;
    document.getElementById(`manager-tab-${next.id}`)?.focus();
  }
</script>

<svelte:head>
  <title>Getting started · Runic Artifex</title>
  <meta
    name="description"
    content="Create a desktop app with C# application logic and your choice of web frontend."
  />
  <meta property="og:title" content="Getting started · Runic Artifex" />
  <meta
    property="og:description"
    content="Create a desktop app with C# application logic and your choice of web frontend."
  />
  <meta name="twitter:title" content="Getting started · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Create a desktop app with C# application logic and your choice of web frontend."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Getting started</p>
    <h1>Build your first Runic app.</h1>
    <p class="lede">
      Use C# for application logic and React, Vue, Svelte or Angular for the
      frontend. The template connects them and opens your app in a browser app
      window.
    </p>
  </section>
  <section class="content-grid shell">
    <ContentCard
      eyebrow="Before you start"
      title="Install the prerequisites"
      full
    >
      <p>
        You need the <a href="https://dotnet.microsoft.com/download/dotnet/10.0"
          >.NET 10 SDK</a
        >
        and a JavaScript package manager: Node.js 24 with npm or pnpm, or Bun 1.4.
        Choose one below; the commands are otherwise the same.
      </p>
      <p>
        The template's window uses CS-WebUI. It opens your app in an installed
        browser in app mode; Chrome, Edge and other Chromium-based browsers work
        best, and Firefox works without app mode. Without a browser it falls
        back to the platform WebView: the Edge WebView2 Runtime on Windows, GTK
        3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS. Run
        <code>dotnet runic doctor</code> in the generated project to check your setup.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow={`SDK ${currentRelease.version}`}
      title="Create and run"
      full
    >
      <div
        class="flex flex-wrap gap-2"
        role="tablist"
        aria-label="JavaScript package manager"
      >
        {#each managers as entry (entry.id)}
          <Button
            id={`manager-tab-${entry.id}`}
            role="tab"
            size="sm"
            variant={selected === entry.id ? 'secondary' : 'ghost'}
            aria-selected={selected === entry.id}
            aria-controls="manager-commands"
            tabindex={selected === entry.id ? 0 : -1}
            onclick={() => (selected = entry.id)}
            onkeydown={selectWithKeyboard}>{entry.label}</Button
          >
        {/each}
      </div>
      <div
        id="manager-commands"
        role="tabpanel"
        aria-labelledby={`manager-tab-${selected}`}
      >
        <pre><code>{quickStart}</code></pre>
        <p>
          Requires the .NET 10 SDK and <a href={manager.href} rel="external"
            >{manager.prerequisite}</a
          >.
        </p>
      </div>
      <p>
        This installs the published template from NuGet; Runic frontend packages
        come from npm. <code>dotnet tool restore</code> installs the project's
        <code>dotnet runic</code>
        tool. <code>dotnet runic dev</code>
        restores the .NET and frontend packages, builds the app, starts the frontend
        development server and opens the app. Frontend edits reload in place; C# edits
        rebuild and restart the app.
      </p>
      <p>
        Replace <code>react</code> with <code>vue</code>, <code>svelte</code> or
        <code>angular</code> to choose your frontend. Vue type checking also
        needs Node.js when using Bun. Plain <code>dotnet build</code> and
        <code>dotnet run</code> work too: the first build installs the frontend packages
        and builds the production frontend.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Make it yours" title="Change the counter" full>
      <p>
        The generated app contains C# ViewModels, a Window and Views that select
        them, and a frontend in <code>Frontend</code>. The build generates a
        typed client for each ViewModel in <code>Frontend/src/generated</code>;
        it is regenerated on every build and not committed. Change a page, then
        follow its client calls into the ViewModel. The frontend owns rendering;
        .NET owns the application model and operation lifetime.
      </p>
      <p>
        <a class="text-link" href={resolve('/views')}
          >Learn about Windows and Views</a
        >, or explore the
        <a
          class="text-link"
          href="https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first"
          >CommunityToolkit Notes example</a
        > for nested content, editing, and navigation.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Share your app" title="Publish for your platform">
      <pre><code>dotnet publish -c Release -r linux-x64</code></pre>
      <p>
        Use <code>win-x64</code>, <code>osx-arm64</code> or another runtime
        identifier for other platforms. The publish folder contains the
        executable and a <code>www</code> folder with the built frontend; distribute
        the whole folder. Users need no JavaScript runtime or package manager, only
        a browser or the platform WebView.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Native windows" title="Choose a host">
      <p>
        The starter uses the CS-WebUI host. For native windows, embedded
        WebViews, file dialogs and other platform services, use the Runic
        Desktop host through <code>Runic.Application.Desktop</code>; the
        <a
          class="text-link"
          href="https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/desktop/host-selection.md"
          >host selection guide</a
        > compares them.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Existing project" title="Add one capability">
      <p>
        You can adopt Application Views, Desktop, Assets, Translations or
        Command Line separately. Choose a package and copy its installation
        command from the <a class="text-link" href={resolve('/packages')}
          >package catalog</a
        >.
      </p>
      <p>
        Application Views uses explicit Window and View types. Its build tooling
        emits the C# attachments and TypeScript client modules for those
        contracts.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Next steps" title="Keep building">
      <p>
        Read the <a class="text-link" href={currentRelease.url} rel="external"
          >release notes</a
        >
        when upgrading preview versions. Keep Runic packages on the same version.
        For SDK contributions, use the
        <a
          class="text-link"
          href="https://github.com/Runic-Artifex/runic-sdk/blob/main/CONTRIBUTING.md"
          >contributor guide</a
        >.
      </p>
    </ContentCard>
  </section>
</div>
