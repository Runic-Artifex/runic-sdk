# RunicToolkit.Hosting.Build

The package contains both the deterministic in-process manifest kernel and executable
`buildTransitive` assets. Set `RunicToolkitGenerateFrontendAssets=true`,
`RunicToolkitFrontendOutputDirectory`, and `RunicToolkitFrontendEntryPoint` to run the
task. `RunicToolkitFrontendAssetMode` supports `Directory`, `Copy`, and `Embed`.

An explicit local frontend command may be supplied through
`RunicToolkitFrontendBuildCommand`; the package never acquires tools or network content.
Set `RunicToolkitVerifyFrontendManifest=true` to fail with `RTKHOST0006` when committed
manifest bytes are missing or stale.

Wave B provides a dependency-neutral, deterministic frontend asset manifest kernel.
The package targets .NET 10, references only `RunicToolkit.Hosting.Abstractions`, and
does not invoke a package manager, load a browser runtime, or depend on MSBuild or
Roslyn APIs.

The complete declared surface is recorded in [PUBLIC-API.md](PUBLIC-API.md).

`FrontendAssetBuildItem` defensively copies input bytes and returns a defensive content
copy. `FrontendAssetManifestBuilder` normalizes application-relative paths to `/`,
rejects empty, rooted, traversal, encoded separator/traversal, control-bearing, and
unsupported URL-like paths, detects case-insensitive collisions, hashes immutable
content with lowercase SHA-256, assigns stable media types, requires exactly one entry
point, requires every declared compressed variant to be present, and sorts output
ordinally.
`FrontendAssetManifestJson` writes compact canonical JSON with a stable contract version
and property order. Directory builds reject reparse points rather than risk following a
link outside the declared output root, and re-check each file immediately before opening
it. Filesystem names whose identity would change during route normalization—leading or
trailing whitespace and literal backslashes on Unix—are also rejected. A cross-platform
handle-level containment proof is not yet available, so an attacker
who can replace filesystem entries concurrently retains a residual time-of-check/time-of-use
risk; untrusted writers must not have access to the frontend output tree during a build.

## Deferred tooling edges

- Wave C owns runtime asset providers and concrete browser/MVVM/command-line adapters.
- Wave D owns opt-in frontend build targets, MSBuild task packaging, tracked-manifest
  verification, incremental build behavior, and any policy that safely permits links.
- Generator/analyzer packaging remains separate and must not introduce Roslyn or MSBuild
  dependencies into this kernel assembly.

The package is MIT licensed. Publication still requires package identity and
release-readiness review.
