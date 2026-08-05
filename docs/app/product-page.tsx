import Link from "next/link";
import type { Product } from "./docs-data";

export function ProductPage({ product }: { product: Product }) {
  return (
    <main>
      <section className="doc-hero shell">
        <div className="breadcrumb"><Link href="/products">Products</Link><span>/</span><span>{product.name}</span></div>
        <div className="product-title-row">
          <span className="product-mark large" aria-hidden="true">{product.mark}</span>
          <div><p className="eyebrow">{product.kicker}</p><h1>{product.name}</h1></div>
        </div>
        <p className="lede">{product.description}</p>
        <div className="actions"><a className="button primary" href={product.source}>View source</a><Link className="button secondary" href="/packages">Package catalog</Link></div>
      </section>

      <div className="doc-layout shell">
        <aside className="on-this-page"><strong>On this page</strong><a href="#choose">When to choose it</a><a href="#boundaries">Boundaries</a><a href="#install">Install</a><a href="#packages">Packages</a></aside>
        <article className="doc-content">
          <section id="choose"><p className="eyebrow">Fit</p><h2>When to choose it</h2><ul className="check-list">{product.bestFor.map((item) => <li key={item}>{item}</li>)}</ul></section>
          <section id="boundaries"><p className="eyebrow">Architecture</p><h2>What stays outside</h2><ul>{product.boundaries.map((item) => <li key={item}>{item}</li>)}</ul></section>
          <section id="install"><p className="eyebrow">Preview</p><h2>Install the first public candidate</h2><div className="notice"><strong>Launch gate</strong><p>These exact versions have verified release candidates. The commands become live when documentation, public repositories, and registry trust are enabled together.</p></div>{product.install?.map((command) => <pre key={command}><code>{command}</code></pre>)}</section>
          <section id="packages"><p className="eyebrow">Owned surface</p><h2>Package family</h2><div className="package-list">{product.packages.map((name) => <code key={name}>{name}</code>)}{product.npmPackages?.map((name) => <code key={name}>{name}</code>)}</div><p>Initial public version: <code>{product.version}</code></p></section>
          <div className="next-card"><span>Next</span><Link href="/architecture">See how product integrations preserve dependency direction →</Link></div>
        </article>
      </div>
    </main>
  );
}
