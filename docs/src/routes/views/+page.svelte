<script lang="ts">
  import ActionLink from '$lib/components/ActionLink.svelte';
  import ContentCard from '$lib/components/ContentCard.svelte';
</script>

<svelte:head>
  <title>Windows and Views · Runic Artifex</title>
  <meta
    name="description"
    content="Compose typed .NET Windows and Views with generated TypeScript clients, then render them with your chosen frontend."
  />
  <meta property="og:title" content="Windows and Views · Runic Artifex" />
  <meta
    property="og:description"
    content="Compose typed .NET Windows and Views with generated TypeScript clients, then render them with your chosen frontend."
  />
  <meta name="twitter:title" content="Windows and Views · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Compose typed .NET Windows and Views with generated TypeScript clients, then render them with your chosen frontend."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Runic Application</p>
    <h1>Compose typed Windows and Views. Render with your frontend.</h1>
    <p class="lede">
      Runic Application Views connects explicit .NET Window and View types to
      ordinary TypeScript clients. The frontend owns its component tree; .NET
      owns ViewModel scopes, logical View lifetimes, commands, and operations.
    </p>
    <div class="actions">
      <ActionLink
        href="https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window"
        >Build the first Window</ActionLink
      >
      <ActionLink
        variant="outline"
        href="https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md"
        >Read the package guide</ActionLink
      >
    </div>
  </section>
  <section class="content-grid shell">
    <ContentCard eyebrow="Composition" title="Select a Window explicitly" full>
      <p>
        A partial <code>RunicWindow&lt;TViewModel&gt;</code> declares the logical
        window boundary. Dependency injection owns the Window's ViewModel scope;
        a host adapter supplies native presentation and lifecycle.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Content" title="Views carry typed context">
      <p>
        A partial <code>RunicView&lt;TViewModel&gt;</code> selects a content
        contract and exposes a typed <code>DataContext</code>. The browser
        acknowledges mount and unmount so .NET can create and dispose logical
        Views as routes change.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Frontend" title="Keep rendering in the framework">
      <p>
        The build emits normal TypeScript modules from the compiled .NET
        application. Use them from plain TypeScript, Svelte, Angular, or another
        frontend without adding a framework-owned transport layer.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Calls" title="Use typed commands and writes">
      <p>
        Generated clients expose snapshots, subscriptions, commands, property
        writes, and disposal. Writes return receipts and version conflicts;
        accepted operations stay owned by .NET through a browser reload.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Examples" title="Compare Window and View patterns" full>
      <p>
        The first Window introduces a single ViewModel and generated client.
        Toolkit Notes exercises nested content and scoped Views. Reactive Notes
        shows routed ViewModels and multiple logical Views for one model.
      </p>
      <div class="actions">
        <ActionLink href="https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first"
          >Toolkit Notes</ActionLink
        >
        <ActionLink variant="outline" href="https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-reactive-views"
          >Reactive Notes</ActionLink
        >
      </div>
    </ContentCard>
  </section>
</div>
