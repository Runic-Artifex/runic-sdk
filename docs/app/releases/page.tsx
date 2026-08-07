import type { Metadata } from "next";

export const metadata: Metadata = { title: "Releases", description: "Initial public preview order and registry safety model for Runic Artifex." };

const train = [
  ["CsWebUi", "2.5.0-beta.4.3", "Already available on nuget.org"],
  ["Runic Command Line", "0.1.0-preview.3.1", "Independent"],
  ["Runic Text Resources", "0.1.0-preview.2.1", "Independent"],
  ["Runic Toolkit", "0.1.0-preview.4.1", "Before Toolkit integrations"],
  ["Runic Flow", "0.1.0-preview.4.1", "After Runic Toolkit"],
  ["Runic Assets", "0.1.0-preview.5.1", "After Runic Toolkit"],
];

export default function ReleasesPage() {
  return <main><section className="page-hero shell"><p className="eyebrow">Release policy</p><h1>Verify once. Publish the same artifacts.</h1><p className="lede">Public registry jobs can start only after clean builds, package consumption, metadata validation, digest recording, and the documentation gate.</p></section><section className="content-grid shell"><div className="release-steps"><article className="release-step"><div><h3>Build from a clean checkout</h3><p>Restore, test, pack, and run applicable frontend and NativeAOT gates without sibling source dependencies.</p><span className="status-pill">Ready</span></div></article><article className="release-step"><div><h3>Validate public metadata</h3><p>Check exact package counts and versions, MIT licensing, README inclusion, tags, repository URL, and the full source commit.</p><span className="status-pill">Ready</span></div></article><article className="release-step"><div><h3>Cross the documentation gate</h3><p>Repositories remain private and public publishing stays disabled until this portal and product guidance are complete.</p><span className="status-pill">In progress</span></div></article><article className="release-step"><div><h3>Enable trusted publishing</h3><p>Use protected environments and OIDC for NuGet and npm. The npm family uses one short-lived bootstrap token only to create its first package records.</p><span className="status-pill">Launch step</span></div></article></div><div className="package-table"><table><thead><tr><th>Product</th><th>First public version</th><th>Order constraint</th></tr></thead><tbody>{train.map(([name, version, constraint]) => <tr key={name}><td>{name}</td><td><code>{version}</code></td><td>{constraint}</td></tr>)}</tbody></table></div></section></main>;
}
