import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Application Bridge",
  description: "The official Effect Schema-first boundary between Runic Toolkit frontends and NativeAOT-safe .NET application handlers.",
};

export default function ApplicationBridgePage() {
  return <main>
    <section className="page-hero shell">
      <p className="eyebrow">Runic Toolkit</p>
      <h1>Named application concepts cross one validated boundary.</h1>
      <p className="lede">The Application Bridge is the official frontend boundary for Runic Toolkit. It replaces generic ViewModel projection with explicit commands, receipts, snapshots, events, and public errors.</p>
      <div className="actions"><a className="button primary" href="https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication">Study the SvelteKit vertical</a><a className="button secondary" href="https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md">Repository guide</a></div>
    </section>
    <section className="content-grid shell">
      <article className="info-card full">
        <p className="eyebrow">Contract pipeline</p><h2>Effect Schema is the authority</h2>
        <div className="mini-flow"><span>Effect Schema<br/><small>encoded wire values</small></span><b>→</b><strong>JSON Schema + manifest<br/><small>committed and fingerprinted</small></strong><b>→</b><span>C# generator<br/><small>reflection-free dispatch</small></span></div>
        <p>Contract generation is deterministic and CI rejects stale artifacts. C# compilation reads only committed artifacts; it never starts Node or infers the public contract from ViewModels or CLR reflection.</p>
      </article>
      <article className="info-card"><p className="eyebrow">Frontend runtime</p><h2>One Effect owner</h2><p>One <code>ManagedRuntime</code> owns the Application Bridge service, transport, scope, and event stream. UI components call a controller instead of scattering Effect execution through the render tree.</p></article>
      <article className="info-card"><p className="eyebrow">Layers</p><h2>Same application interface</h2><p><code>CsWebUiApplicationBridgeLive</code>, <code>MockApplicationBridge</code>, and fault-injection Layers expose the same semantics. Renderer code remains independent of the active transport.</p></article>
      <article className="info-card"><p className="eyebrow">Host runtime</p><h2>NativeAOT by construction</h2><p>Generated DTOs, strict codecs, exhaustive dispatch, and typed event publishers avoid reflection. Sessions own bounded admission, revisions, sequence numbers, duplicate rejection, operations, and deterministic teardown.</p></article>
      <article className="info-card"><p className="eyebrow">Security</p><h2>Privileged choices remain native</h2><p>The frontend can request destination selection but cannot send a privileged path. The host returns an opaque selection ID plus display-safe metadata and validates that handle when work begins.</p></article>
      <article className="info-card full"><p className="eyebrow">Official frontend path</p><h2>Svelte 5, SvelteKit, Vite 8, and Vite DevTools</h2><p><code>@runic-artifex/svelte</code> projects one Application Bridge controller into Svelte 5 runes and context. <code>@runic-artifex/sveltekit</code> owns the static SPA adapter and native-host page options. <code>@runic-artifex/vite-plugin-runic-toolkit</code> owns Toolkit development metadata, bounded inspection, and HMR resources. The official <code>@vitejs/devtools</code> plugin remains the DevTools host and is excluded from production output.</p><p>These integrations intentionally support Svelte 5 only. They do not carry compatibility code for pre-runes Svelte releases, and framework adapters do not duplicate transport, reconnect, or protocol state.</p></article>
      <article className="info-card full"><p className="eyebrow">Wire vocabulary</p><h2>Describe the application, not its implementation</h2><div className="package-list"><code>InitializeApplication</code><code>SelectDestination</code><code>Navigate</code><code>StartInstallation</code><code>OperationProgress</code><code>OperationCompleted</code><code>OperationFailed</code><code>CancelOperation</code></div><p>There is no generic <code>setProperty</code>, numeric member identifier, or ViewModel <code>execute</code> operation. Long-running work returns an operation ID promptly; progress and terminal outcomes arrive through the validated Effect Stream. Cancellation is an explicit protocol action.</p></article>
      <article className="info-card full"><p className="eyebrow">Install</p><h2>Consume the verified private candidates</h2><pre><code>dotnet add package RunicToolkit.Hosting.CsWebUi.ApplicationBridge --version 0.1.0-preview.21.1</code></pre><pre><code>dotnet add package RunicToolkit.ApplicationBridge.Generators --version 0.1.0-preview.21.1</code></pre><pre><code>npm install @runic-artifex/application-bridge@0.1.0-preview.21.1 @runic-artifex/svelte@0.1.0-preview.7.1 svelte@5.56.8 effect@3.22.1</code></pre><pre><code>npm install -D @runic-artifex/sveltekit@0.1.0-preview.7.1 @runic-artifex/vite-plugin-runic-toolkit@0.1.0-preview.7.1 @vitejs/devtools@0.4.12 vite@8.2.1</code></pre><p>The package commands become public at launch. Until then, authenticated GitHub Packages access is required.</p></article>
      <article className="info-card full"><p className="eyebrow">Reference proof</p><h2>The package-only Setup applications close the loop</h2><p>The neutral and SvelteKit reference applications cover initialization, backend-authoritative navigation, opaque destination selection, installation receipts, progress, completion, failure, cancellation, recovery, development DevTools, deterministic production frontend output, NativeAOT, and real native-browser roundtrips.</p></article>
    </section>
  </main>;
}
