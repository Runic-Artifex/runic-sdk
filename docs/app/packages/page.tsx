import type { Metadata } from "next";
import Link from "next/link";
import { products } from "../docs-data";

export const metadata: Metadata = { title: "Package catalog", description: "The NuGet and npm packages owned by each Runic Artifex product." };

const frontendIntegrations = [
  { name: "@runic-artifex/svelte", owner: "runic-svelte", version: "0.1.0-preview.7.1", source: "https://github.com/Runic-Artifex/runic-svelte" },
  { name: "@runic-artifex/sveltekit", owner: "runic-svelte", version: "0.1.0-preview.7.1", source: "https://github.com/Runic-Artifex/runic-svelte" },
  { name: "@runic-artifex/vite-plugin-runic-toolkit", owner: "runic-vite", version: "0.1.0-preview.7.1", source: "https://github.com/Runic-Artifex/runic-vite" },
] as const;

export default function PackagesPage() {
  const rows = products.flatMap((product) => [...product.packages.map((name) => ({ name, registry: "NuGet", product })), ...(product.npmPackages ?? []).map((name) => ({ name, registry: "npm", product }))]);
  return <main><section className="page-hero shell"><p className="eyebrow">Package catalog</p><h1>One owner for every public package.</h1><p className="lede">Package families release independently. Integration repositories own how their framework connects to Toolkit.</p></section><section className="content-grid shell"><div className="package-table"><table><thead><tr><th>Package</th><th>Registry</th><th>Owner</th><th>First public candidate</th></tr></thead><tbody>{rows.map(({ name, registry, product }) => <tr key={`${registry}:${name}`}><td>{name}</td><td>{registry}</td><td><Link href={`/products/${product.slug}`}>{product.name}</Link></td><td><code>{product.version}</code></td></tr>)}{frontendIntegrations.map((integration) => <tr key={`npm:${integration.name}`}><td>{integration.name}</td><td>npm</td><td><a href={integration.source}>{integration.owner}</a></td><td><code>{integration.version}</code></td></tr>)}</tbody></table></div><article className="info-card full"><p className="eyebrow">Version policy</p><h2>Exact during preview, explicit afterward</h2><p>Each repository releases one version across its owned package family. Cross-product dependencies remain exact while contracts settle. Applications decide their own update cadence.</p></article></section></main>;
}
