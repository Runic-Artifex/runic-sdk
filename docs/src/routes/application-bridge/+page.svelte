<svelte:head>
  <title>Application Bridge · Runic Artifex</title>
  <meta
    name="description"
    content="The official Effect Schema-first boundary between Runic Toolkit frontends and NativeAOT-safe .NET application handlers."
  />
</svelte:head>

<main>
  <section class="page-hero shell">
    <p class="eyebrow">Runic Toolkit</p>
    <h1>Named application concepts cross one validated boundary.</h1>
    <p class="lede">
      The Application Bridge is the official frontend boundary for Runic
      Toolkit. It replaces generic ViewModel projection with explicit commands,
      receipts, snapshots, events, and public errors.
    </p>
    <div class="actions">
      <a
        class="button primary"
        href="https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication"
        >Study the SvelteKit vertical</a
      >
      <a
        class="button secondary"
        href="https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md"
        >Repository guide</a
      >
    </div>
  </section>
  <section class="content-grid shell">
    <article class="info-card full">
      <p class="eyebrow">Contract pipeline</p>
      <h2>Effect Schema is the authority</h2>
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
    </article>
    <article class="info-card">
      <p class="eyebrow">Frontend runtime</p>
      <h2>One Effect owner</h2>
      <p>
        One <code>ManagedRuntime</code> owns the Application Bridge service, transport,
        scope, and event stream. UI components call a controller instead of scattering
        Effect execution through the render tree.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Layers</p>
      <h2>Same application interface</h2>
      <p>
        <code>CsWebUiApplicationBridgeLive</code>,
        <code>MockApplicationBridge</code>, and fault-injection Layers expose
        the same semantics. Renderer code remains independent of the active
        transport.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Host runtime</p>
      <h2>NativeAOT by construction</h2>
      <p>
        Generated DTOs, strict codecs, exhaustive dispatch, and typed event
        publishers avoid reflection. Sessions own bounded admission, revisions,
        sequence numbers, duplicate rejection, operations, and deterministic
        teardown.
      </p>
    </article>
    <article class="info-card">
      <p class="eyebrow">Security</p>
      <h2>Privileged choices remain native</h2>
      <p>
        The frontend can request destination selection but cannot send a
        privileged path. The host returns an opaque selection ID plus
        display-safe metadata and validates that handle when work begins.
      </p>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Official frontend path</p>
      <h2>Svelte 5, SvelteKit, Vite 8, and Vite DevTools</h2>
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
    </article>
    <article class="info-card full">
      <p class="eyebrow">Wire vocabulary</p>
      <h2>Describe the application, not its implementation</h2>
      <div class="package-list">
        <code>InitializeApplication</code><code>SelectDestination</code><code
          >Navigate</code
        ><code>StartInstallation</code><code>OperationProgress</code><code
          >OperationCompleted</code
        ><code>OperationFailed</code><code>CancelOperation</code>
      </div>
      <p>
        There is no generic <code>setProperty</code>, numeric member identifier,
        or ViewModel
        <code>execute</code> operation. Long-running work returns an operation ID
        promptly; progress and terminal outcomes arrive through the validated Effect
        Stream.
      </p>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Install</p>
      <h2>Wait for the refreshed public candidates</h2>
      <div class="notice">
        <strong>Publication pending</strong>
        <p>
          Toolkit, Svelte, and Vite candidates are being rebuilt from their
          final source commits. Exact install commands will return here after
          the matching NuGet and npm artifacts pass clean downstream consumption
          and trusted-publisher setup.
        </p>
      </div>
    </article>
    <article class="info-card full">
      <p class="eyebrow">Reference proof</p>
      <h2>The package-only Setup applications close the loop</h2>
      <p>
        The neutral and SvelteKit reference applications cover initialization,
        backend-authoritative navigation, opaque destination selection,
        installation receipts, progress, completion, failure, cancellation,
        recovery, development DevTools, deterministic production frontend
        output, NativeAOT, and real native-browser roundtrips.
      </p>
    </article>
  </section>
</main>
