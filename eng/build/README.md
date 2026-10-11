# Managed build policies

The root `Directory.Build.props` supplies workspace identity and `RunicSdkRoot`.
Managed projects explicitly import their policy from this directory, so moving a
project does not silently change its language, analyzer or package settings.

- `application`: View runtime (`Runic.Application`), CS-WebUI, Desktop, ReactiveUI and
  testing adapters, the BridgeCodegen tools, `dotnet-runic`, templates, the
  `Runic.Platform*` native-service packages and their tests.
- `assets`: archive/runtime adapters and asset packer.
- `desktop`: `Runic.Desktop`/`Runic.Desktop.Gtk4`, their tests and native smoke checks.

`application` and `assets` are thin wrappers over
`common.props`/`common.targets`. They differ only in package tags, icon and the name
of their build-mode switch (`RunicApplicationBuildMode`, `RunicAssetsBuildMode`).
`Development` is the default; CI selects `Verification`, which treats warnings as errors and enables
NuGet audit and trim/AOT analyzers for shipping projects. A shipping project sets
`Runic<Component>ShippingProject`, which also marks it trimmable and AOT-compatible
unless it sets `RunicTrimmable=false` (the test-only `Runic.Application.Testing` does);
build-time tools such as BridgeCodegen leave it unset. Package versions come
from `eng/Versions.props`. Desktop keeps its separate policy and sets
`IsAotCompatible` and `IsTrimmable` itself. Package consumers receive
`Runic.Desktop.targets` through `buildTransitive`; repository projects that
reference `Runic.Desktop` directly or transitively set
`RunicImportDesktopTargets=true` instead, and `eng/desktop-targets.test.mjs`
keeps that list exact. `bun eng/generate-shipping-projects.mjs --check`
fails when a listed shipping project lacks its policy's flag; template packages,
which contain no assemblies, are exempt.

Props are imported before a project's property groups. Matching targets are imported
after its items. Root `Directory.Build.targets` applies Desktop's host profile
switch to source consumers. Desktop takes only WebView2's native loader assets;
its COM callbacks are generated at build time for NativeAOT. NuGet dependency
versions remain in the root `Directory.Packages.props`.

Packing pins dependencies on shipping workspace projects to exact NuGet versions.
The project inventory in `shipping-projects.props` is generated from
`eng/workspace.json`; regenerate it with `bun eng/generate-shipping-projects.mjs`
when that inventory changes. External dependency ranges are preserved. Explicit
internal `PackageReference` entries must also use exact `[version]` ranges.

## Public API

`public-api.targets` applies to every shipping library (tools and template packages
expose no referenceable assemblies). It references
`Microsoft.CodeAnalysis.PublicApiAnalyzers`, which compares the compiled public
surface with the project's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`.
These files are analyzer inputs and are not packed. A new or changed public member
without a matching entry reports RS0016, a removed one RS0017; `Verification` builds
treat both as errors. Add new API to `PublicAPI.Unshipped.txt` (the IDE code fix does
this) and mark removals with `*REMOVED*` there.

Packing also runs package validation against `RunicPackageValidationBaselineVersion`
from `eng/Versions.props`, the last published release. An intentional binary break
is recorded in the project's `CompatibilitySuppressions.xml`; regenerate it with
`dotnet pack <project> -p:ApiCompatGenerateSuppressionFile=true`.
A package that has never been published sets `<RunicPackageValidationBaselineMissing>true</RunicPackageValidationBaselineMissing>`,
which skips the baseline comparison, and `<RunicPackageFirstReleaseVersion>` to its first release.
`eng/workspace.test.mjs` fails once the baseline reaches that release, so the opt-out cannot outlive it.

## Release links

`release-links.targets` keeps links that leave a package on the release tag,
`v$(PackageVersion)`, instead of `main`:

- Packing a project with a `PackageReadmeFile` packs a copy of the README in which
  this repository's `blob/main` and `tree/main` links name the tag and relative
  links become absolute links at the tag. A remaining `main` link of any Runic
  Artifex repository fails the pack; link to the package registry or the
  documentation portal instead. `eng/release/readme-links.mjs` applies the same
  rules to npm packages, and `bun run pack` checks every staged README.
- `<RunicGenerateReleaseLinks>true</RunicGenerateReleaseLinks>` compiles an internal
  `RunicReleaseLinks` class. `RunicReleaseLinks.Diagnostic(id)` is the ID's entry
  in [docs/diagnostics.md](../../docs/diagnostics.md) at the tag, so a new
  diagnostic needs only its catalog entry. With `RunicExperimentalDiagnosticId`,
  `RunicReleaseLinks.ExperimentalUrl` is the `UrlFormat` of the project's
  `[Experimental]` attributes.
- `<RunicPackDiagnosticsHelpLinks>true</RunicPackDiagnosticsHelpLinks>` packs a
  `buildTransitive` props file that sets `RunicDiagnosticsHelpLinkBase` at the tag,
  for `HelpLink="$(RunicDiagnosticsHelpLinkBase)<lowercase ID>"` on the package's
  coded `Error` and `Warning` tasks.

`eng/diagnostics.test.mjs` fails when a reported ID has no catalog entry, a catalog
entry is stale, or a reporting project does not link to the catalog.

Use root commands and the artifact/component inventory in `eng/workspace.json`.
