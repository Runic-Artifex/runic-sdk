import type { Metadata } from "next";

export const metadata: Metadata = { title: "Architecture", description: "The ownership and dependency rules that keep Runic Artifex products independent." };

export default function ArchitecturePage() {
  return <main><section className="page-hero shell"><p className="eyebrow">Architecture</p><h1>Ownership and dependency direction are separate decisions.</h1><p className="lede">A product owns the behavior it contributes. An integration depends on both cores. Neither core depends back on the integration.</p></section><section className="content-grid shell">
    <article className="info-card full"><p className="eyebrow">Canonical seam</p><h2>Flow controls its Toolkit integration</h2><div className="mini-flow"><span>RunicFlow<br/><small>UI-neutral core</small></span><b>→</b><strong>RunicFlow.RunicToolkit<br/><small>Owned by Flow</small></strong><b>←</b><span>RunicToolkit.Desktop<br/><small>Extension contracts</small></span></div></article>
    <article className="info-card"><p className="eyebrow">Product autonomy</p><h2>Independent histories</h2><p>Flow, Assets, Text Resources, Command Line, Toolkit, and CsWebUi each version and release from their own repository. A change in one product does not pollute every other product history.</p></article>
    <article className="info-card"><p className="eyebrow">Stable seams</p><h2>Explicit compatibility</h2><p>Integrations pin exact cross-product versions during the preview. Compatibility is evidence recorded by package consumers, frontend builds, and applicable NativeAOT runs—not implied by synchronized versions.</p></article>
    <article className="info-card"><p className="eyebrow">Framework neutrality</p><h2>Cores stay portable</h2><p>Runic Flow has no UI dependency. Runic Assets has no host dependency. Runic Text Resources begins with .NET but defines language-neutral contracts.</p></article>
    <article className="info-card"><p className="eyebrow">Integration ownership</p><h2>Behavior lives with its author</h2><p><code>RunicAssets.RunicToolkit</code> and <code>RunicFlow.RunicToolkit</code> live and release with Assets and Flow respectively.</p></article>
    <article className="info-card full"><p className="eyebrow">Application boundary</p><h2>Schema first, renderer last</h2><p>Effect Schema is the authority for Application Bridge wire values. Deterministic JSON Schema and a canonical manifest feed the reflection-free C# generator. React, Vue, Svelte, or Angular then project validated application events into their own state systems without owning transport lifecycle.</p></article>
  </section></main>;
}
