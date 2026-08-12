<script lang="ts">
  import ActionLink from '$lib/components/ActionLink.svelte';
  import ContentCard from '$lib/components/ContentCard.svelte';
  import Notice from '$lib/components/Notice.svelte';
</script>

<svelte:head>
  <title>Application Bridge · Runic Artifex</title>
  <meta
    name="description"
    content="Connect browser frontends to NativeAOT-safe .NET hosts with explicit commands, validated events, and generated contracts."
  />
  <meta property="og:title" content="Application Bridge · Runic Artifex" />
  <meta
    property="og:description"
    content="Connect browser frontends to NativeAOT-safe .NET hosts with explicit commands, validated events, and generated contracts."
  />
  <meta name="twitter:title" content="Application Bridge · Runic Artifex" />
  <meta
    name="twitter:description"
    content="Connect browser frontends to NativeAOT-safe .NET hosts with explicit commands, validated events, and generated contracts."
  />
</svelte:head>

<div>
  <section class="page-hero shell">
    <p class="eyebrow">Runic Toolkit</p>
    <h1>
      Connect a frontend to .NET through one validated application contract.
    </h1>
    <p class="lede">
      Application Bridge carries named commands, receipts, snapshots, events,
      and public errors between a frontend and a NativeAOT-safe .NET host. It
      avoids exposing ViewModels or generic property operations as the public
      contract.
    </p>
    <div class="actions">
      <ActionLink
        href="https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication"
        >View the SvelteKit example</ActionLink
      >
      <ActionLink
        variant="outline"
        href="https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md"
        >Read the repository guide</ActionLink
      >
    </div>
  </section>
  <section class="content-grid shell">
    <ContentCard
      eyebrow="What crosses the boundary"
      title="Describe the application, not its implementation"
      full
    >
      <div class="package-list">
        <code>InitializeApplication</code><code>SelectDestination</code><code
          >Navigate</code
        ><code>StartInstallation</code><code>OperationProgress</code><code
          >OperationCompleted</code
        ><code>OperationFailed</code><code>CancelOperation</code>
      </div>
      <p>
        There is no generic <code>setProperty</code>, numeric member identifier,
        or ViewModel <code>execute</code> operation. Long-running work returns an
        operation ID promptly; progress and terminal outcomes arrive through the validated
        Effect Stream.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Contracts become code"
      title="Effect Schema is the source of truth"
      full
    >
      <div class="mini-flow">
        <span>Effect Schema<br /><small>encoded wire values</small></span><b
          >→</b
        ><strong
          >JSON Schema + manifest<br /><small>committed and fingerprinted</small
          ></strong
        ><b>→</b><span
          >C# generator<br /><small>reflection-free dispatch</small></span
        >
      </div>
      <p>
        Contract generation is deterministic and CI rejects stale artifacts. C#
        compilation reads only committed artifacts; it never starts Node or
        infers the public contract from ViewModels or CLR reflection.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Frontend runtime" title="One runtime owns the bridge">
      <p>
        One <code>ManagedRuntime</code> owns the Application Bridge service, transport,
        scope, and event stream. UI components call a controller instead of scattering
        Effect execution through the render tree.
      </p>
    </ContentCard>
    <ContentCard eyebrow="Layers" title="Change transports, not semantics">
      <p>
        <code>CsWebUiApplicationBridgeLive</code>,
        <code>MockApplicationBridge</code>, and fault-injection Layers expose
        the same semantics. Renderer code remains independent of the active
        transport.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Host runtime"
      title="Stay NativeAOT-safe by construction"
    >
      <p>
        Generated DTOs, strict codecs, exhaustive dispatch, and typed event
        publishers avoid reflection. Sessions own bounded admission, revisions,
        sequence numbers, duplicate rejection, operations, and deterministic
        teardown.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Security"
      title="Keep privileged choices in the native host"
    >
      <p>
        The frontend can request destination selection but cannot send a
        privileged path. The host returns an opaque selection ID plus
        display-safe metadata and validates that handle when work begins.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Official frontend path"
      title="Use the official Svelte and Vite path"
      full
    >
      <p>
        The official frontend packages cover Svelte state, SvelteKit
        configuration, and Vite development tooling.
      </p>
      <p>
        <code>@runic-artifex/svelte</code> projects one Application Bridge
        controller into Svelte 5 runes and context.
        <code>@runic-artifex/sveltekit</code>
        owns the static SPA adapter and native-host page options.
        <code>@runic-artifex/vite-plugin-runic-toolkit</code>
        owns Toolkit development metadata, bounded inspection, and HMR resources.
        The official
        <code>@vitejs/devtools</code> plugin remains the DevTools host and is excluded
        from production output.
      </p>
      <p>
        These integrations intentionally support Svelte 5 only. Framework
        adapters do not duplicate transport, reconnect, or protocol state.
      </p>
    </ContentCard>
    <ContentCard
      eyebrow="Availability"
      title="Install the published integrations"
      full
    >
      <Notice title="Available on NuGet and npm">
        <p>Pin the exact preview versions for reproducible applications:</p>
        <ul>
          <li>
            Runic Toolkit <code>0.1.0-preview.30.1</code> — Available on NuGet and
            npm
          </li>
          <li>
            Runic Svelte <code>0.1.0-preview.14.1</code> — Available on npm
          </li>
          <li>
            Runic Vite <code>0.1.0-preview.8.1</code> — Available on npm
          </li>
        </ul>
        <pre><code
            >dotnet add package RunicToolkit.ApplicationBridge --version 0.1.0-preview.30.1
npm install @runic-artifex/application-bridge@0.1.0-preview.30.1 @runic-artifex/svelte@0.1.0-preview.14.1 @runic-artifex/vite-plugin-runic-toolkit@0.1.0-preview.8.1</code
          ></pre>
      </Notice>
    </ContentCard>
    <ContentCard
      eyebrow="Reference applications"
      title="See the neutral and SvelteKit paths end to end"
      full
    >
      <p>
        The neutral and SvelteKit reference applications cover initialization,
        backend-authoritative navigation, opaque destination selection,
        installation receipts, progress, completion, failure, cancellation,
        recovery, development DevTools, deterministic production frontend
        output, NativeAOT, and real native-browser roundtrips.
      </p>
    </ContentCard>
  </section>
</div>
