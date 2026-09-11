<script lang="ts">
  import { resolve } from '$app/paths';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import {
    catalogRows,
    packageInstallCommand,
    currentRelease,
  } from '$lib/release-docs';
  const template = catalogRows.find(
    (entry) => entry.name === 'Runic.Application.Templates',
  )!;
  const quickStart = `${packageInstallCommand(template)}
dotnet new runic-app-svelte --name MyApp --packageManager bun
cd MyApp
dotnet tool restore
dotnet runic doctor
dotnet run`;
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
      frontend. The template connects them and opens your app in a desktop
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
        and <a href="https://bun.sh">Bun 1.4 or later</a> for the commands
        below. You can also use Node.js 24 with npm or pnpm by changing
        <code>--packageManager</code>.
      </p>
      <p>
        Run <code>dotnet runic doctor</code> in the generated project to see the
        native dependencies for your operating system. The Linux templates use
        GTK 3 and WebKitGTK 4.1. For GTK 4 and WebKitGTK 6, add the optional
        GTK4 adapter and follow the
        <a
          href="https://github.com/Runic-Artifex/runic-sdk/blob/main/eng/release/notes/gtk4-portals-follow-up.md"
          >GTK4 and portal migration guide</a
        >. Windows needs the Edge WebView2 Runtime; NativeAOT builds include the
        loader without a separate WebView2Loader.dll. macOS uses its system
        WebView; live testing of the new platform services is still pending.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow={`SDK ${currentRelease.version}`}
      title="Create and run"
      full
    >
      <pre><code>{quickStart}</code></pre>
      <p>
        This installs the published template from NuGet. Runic frontend packages
        come from npm; no GitHub package feed or token is needed. The first run
        restores dependencies and starts the frontend development server.
      </p>
      <p>
        Replace <code>svelte</code> with <code>react</code>, <code>vue</code> or
        <code>angular</code> to choose your frontend. Vue type checking also needs
        Node.js when using Bun.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Make it yours" title="Change the counter" full>
      <p>
        The generated app contains a counter, a typed Application Bridge
        contract and a C# command handler. Change the page in <code
          >Frontend</code
        >, then follow its increment command into the host. UI state stays in
        your frontend; application commands run in C#.
      </p>
      <p>
        <a class="text-link" href={resolve('/application-bridge')}
          >Learn how the bridge works</a
        >, or explore the
        <a
          class="text-link"
          href="https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/document-migration"
          >document application example</a
        > for editing, saving and native file dialogs.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Share your app" title="Publish for your platform">
      <pre><code>dotnet publish -c Release</code></pre>
      <p>
        Publishing embeds the static frontend in the application. Users do not
        need a JavaScript runtime or package manager. The target platform’s
        native WebView dependencies still apply.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Existing project" title="Add one capability">
      <p>
        You can adopt Application, Desktop, Assets, Translations or Command Line
        separately. Choose a package and copy its installation command from the <a
          class="text-link"
          href={resolve('/packages')}>package catalog</a
        >.
      </p>
      <p>
        The Application Bridge source generator is included with <code
          >Runic.Application.Bridge</code
        >; it does not need a separate package install.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Next steps" title="Keep building" full>
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
