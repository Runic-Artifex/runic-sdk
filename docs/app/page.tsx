import type { Metadata } from "next";
import Link from "next/link";
import { products } from "./docs-data";

export const metadata: Metadata = {
  title: "Build one application model across .NET surfaces",
  description:
    "Runic Artifex is a family of independent, NativeAOT-minded tools for application architecture, UI, flow, assets, localization, and command lines.",
};

export default function Home() {
  return (
    <main>
      <section className="hero shell">
        <div className="hero-copy">
          <p className="eyebrow">Runic Artifex ecosystem</p>
          <h1>Build one application model across .NET surfaces.</h1>
          <p className="lede">
            Independent tools for WebUI, desktop, application bridges, navigation, assets,
            localization, and command lines—designed to compose without sharing
            one release train.
          </p>
          <div className="actions">
            <Link className="button primary" href="/getting-started">
              Start with the preview
            </Link>
            <Link className="button secondary" href="/architecture">
              Understand the boundaries
            </Link>
          </div>
        </div>
        <div className="hero-map" aria-label="Runic Artifex product relationship summary">
          <div className="map-core">
            <span>Application composition</span>
            <strong>Runic Toolkit</strong>
          </div>
          <div className="map-ring">
            <span>Bridge</span><span>Flow</span><span>Assets</span>
            <span>Text</span><span>CLI</span><span>CsWebUi</span>
          </div>
          <p>Products own their cores and official integrations.</p>
        </div>
      </section>

      <section className="section shell">
        <div className="section-heading split-heading">
          <div>
            <p className="eyebrow">Choose your layer</p>
            <h2>Use one product or compose the ecosystem.</h2>
          </div>
          <Link className="text-link" href="/products">Compare all products →</Link>
        </div>
        <div className="product-grid">
          {products.map((product) => (
            <Link className="product-card" href={`/products/${product.slug}`} key={product.slug}>
              <span className="product-mark" aria-hidden="true">{product.mark}</span>
              <p className="kicker">{product.kicker}</p>
              <h3>{product.name}</h3>
              <p>{product.summary}</p>
              <span className="card-link">Explore {product.shortName} →</span>
            </Link>
          ))}
        </div>
      </section>

      <section className="section shell">
        <div className="principle-panel">
          <div>
            <p className="eyebrow">The ownership rule</p>
            <h2>Integrations belong to the product doing the integrating.</h2>
          </div>
          <div className="mini-flow" aria-label="Runic Flow integration dependency direction">
            <span>RunicFlow</span>
            <b>→</b>
            <strong>RunicFlow.RunicToolkit</strong>
            <b>←</b>
            <span>RunicToolkit.Desktop</span>
          </div>
          <p>
            Cores remain portable. Adapters depend on both sides. Git history,
            versioning, and release decisions stay with the product that owns the
            behavior.
          </p>
        </div>
      </section>

      <section className="section shell launch-strip">
        <div>
          <p className="eyebrow">First public preview</p>
          <h2>Release candidates and documentation are ready for launch.</h2>
          <p>
            Every package family has a guarded, verify-only release path with MIT
            metadata, repository provenance, clean consumption, and applicable
            NativeAOT gates.
          </p>
        </div>
        <Link className="button secondary" href="/releases">See the release train</Link>
      </section>
    </main>
  );
}
