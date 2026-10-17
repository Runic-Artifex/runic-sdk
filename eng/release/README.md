# Releasing Runic SDK

This is the SDK's current release policy. It replaces the first-preview acceptance
plans and the SDK portions of the organization launch/release runbooks. Historical
releases and their evidence remain historical records.

## Routine preview release

1. Commit the intended preview version and user-facing changes. Package versions
   must agree with `eng/workspace.json`; published versions cannot be overwritten.
2. Wait for the `ci.yml` push run of that commit on `main` to succeed.
3. Optionally run **Publish preview** (`publish-preview.yml`) on `main` with
   **dry-run** checked. It performs every check below and writes nothing.
4. Run **Publish preview** on `main`, entering that version.
5. The workflow finds the successful CI push run of the dispatched commit, downloads
   its `runic-sdk-<run id>` package artifact, verifies the inventory against the
   requested version and commit, attests their build provenance and SBOM, publishes
   those exact packages, creates the GitHub prerelease with generated change notes, a
   package bundle, a CycloneDX SBOM and one checksum file, and moves npm `latest`.
6. Wait for the run's `indexed` job: it ends once nuget.org lists every NuGet package
   of the release, which can take up to an hour after the push. Until then a restore of
   the new version fails with NU1102, so push the post-release commit after it. Then
   move each library's `PublicAPI.Unshipped.txt` entries into
   `PublicAPI.Shipped.txt`. A `*REMOVED*` entry is not moved: delete it together
   with the Shipped line it names. Then set `RunicPackageValidationBaselineVersion`
   in `eng/Versions.props` to the published version and delete the
   `CompatibilitySuppressions.xml` files, which describe breaks from the old baseline.
   Remove `<RunicPackageValidationBaselineMissing>` and `<RunicPackageFirstReleaseVersion>`
   from each package published for the first time (for example `Runic.Navigation` after
   0.7.0-preview.4), so its next release is validated against this one; the engineering
   tests fail until you do. Likewise remove `<PackageValidationBaselineName>` from a
   package published for the first time under a new ID (the `Runic.Application.Views*`
   packages after 0.7.0-preview.7), which until then validates against its old ID.
   Also remove the RS0026 `SuppressMessage` attributes on
   `IRunicModelContext.InvokeAsync` and `RunicModelContext.InvokeAsync` once their lines are in
   `Runic.Navigation/PublicAPI.Shipped.txt`: the analyzer accepts shipped overloads.
7. Refresh the docs catalog (see the end of this page).

Full CI includes package/template consumers and native JIT/NativeAOT checks. The
release workflow does not run it again; it fails before publishing if the commit has
no successful CI push run, if that run is still in progress, or if its package
artifact has expired. There is no separate acceptance workflow,
receipt upload, candidate approval dossier, mandatory soak, or independent pilot
prerequisite. Manual checks are appropriate when a change affects native/UI
behavior that automation cannot cover. Soaks and matched benchmarks remain useful
for relevant changes and regression investigations; they are not routine gates.

Keep release notes focused on changes, installation, migration and meaningful
known issues. Put logs and internal package metadata in Actions artifacts. Record
nonblocking limitations as issues rather than introducing custom waiver formats.

## Workflow jobs and dry run

- `preflight` (`contents: read`, `actions: read`) checks the requested version and
  runs `eng/release/ci-run.mjs` to find the CI run and the id of its package artifact.
- `candidate` (`contents: read`, `actions: read`, no environment) downloads the
  artifact with `actions/download-artifact` (`artifact-ids`, `run-id`, `github-token`;
  a re-upload under the same name gets a new id, so it cannot swap the bytes), records it
  with `cli.mjs prepare`, and writes the release assets with `github.mjs describe`:
  the package bundle, a CycloneDX 1.6 SBOM (`sbom.py`, standard library only; it
  reads package metadata and executes nothing) and `SHA256SUMS`. Bundle and SBOM are
  deterministic for a commit. It uploads these files as `release-candidate-<run id>`
  and passes their `sha256sum` lines to `publish` as the job output `release-sha256`.
  It then runs the publication checks read-only: `cli.mjs publish --dry-run`
  (which versions are missing; published versions must have matching contents; the
  NuGet trusted-publishing key could push every missing package, see
  [New NuGet package IDs](#new-nuget-package-ids)),
  `github.mjs release --dry-run` (the tag and any release belong to this commit) and
  `cli.mjs tag-latest --dry-run` (where npm `latest` would move).
- `publish` runs only when **dry-run** is unchecked. It is the only job in the
  `preview` environment and the only one with `id-token: write`,
  `attestations: write` and `contents: write`. It downloads the same artifact and
  the candidate files, checks the files against `release-sha256`, verifies the
  packages against the candidate inventory, and attests those verified bytes before
  anything is published: `actions/attest-build-provenance` covers every `.nupkg`,
  every npm `.tgz` and the three release assets; `actions/attest` attaches the SBOM
  to the packages and the bundle. It repeats the registry lookups and the
  NuGet push check before the first push, then publishes (npm also with `--provenance`),
  creates the release and moves `latest`. It installs no workspace dependencies:
  the release scripts use only built-in modules.
- `indexed` (`contents: read`, no environment) runs after `publish` and polls
  `cli.mjs wait-indexed`: every 30 seconds, for up to an hour, it reads each NuGet
  package's flat-container version list, which `dotnet restore` uses, until all list
  the release's version. The GitHub release and npm `latest` do not wait for it.

A dry run therefore ends after `candidate`, with no OIDC token, attestation,
registry write, tag or release. A failing dry-run check is the failure the real
run would hit. `eng/release/workflow.test.mjs`, `github.test.mjs`, `sbom.test.mjs`
and `ci-run.test.mjs` pin this contract.

## Verifying a release

Every package and release asset has a signed build-provenance attestation from
`publish-preview.yml` on `main`, and the packages and bundle have an SBOM
attestation. Verify with the GitHub CLI (`gh auth login` first):

```sh
version=0.7.0-preview.1
gh release download "v$version" -R Runic-Artifex/runic-sdk
gh attestation verify "runic-sdk-$version-packages.tar.gz" -R Runic-Artifex/runic-sdk
tar -xzf "runic-sdk-$version-packages.tar.gz"
gh attestation verify "nuget/Runic.Application.$version.nupkg" -R Runic-Artifex/runic-sdk \
  --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml
# The SBOM attestation (CycloneDX); the release asset runic-sdk-<version>.cdx.json is the same document.
gh attestation verify "nuget/Runic.Application.$version.nupkg" -R Runic-Artifex/runic-sdk \
  --predicate-type https://cyclonedx.org/bom
# npm serves the published bytes unchanged, so a registry tarball verifies directly.
npm pack "@runic-artifex/views@$version"
gh attestation verify "runic-artifex-views-$version.tgz" -R Runic-Artifex/runic-sdk
```

`sha256sum --check SHA256SUMS` checks the bundle and SBOM. NuGet.org adds its
repository signature (`.signature.p7s`) to every package it accepts, so a `.nupkg`
downloaded from NuGet.org has different bytes from the attested one and does not
verify by itself. Verify the copy from the release bundle; every entry of the
NuGet.org package except `.signature.p7s` is identical to it, and
`dotnet nuget verify --all <package>` checks the NuGet.org signature.

Rerunning Publish preview for a version packed with the old flow, such as
0.6.0-preview.1, fails its registry check, also in a dry run. Packing now re-gzips npm archives to stamp
`gitHead`, so even the same commit yields npm bytes that differ from the published
ones, and the registry check fails with `Registry bytes differ` (on today's
`main`, NuGet contents and the existing `v0.6.0-preview.1` tag differ as well).
Commit the next preview version first; a dry run is meaningful from then on.

## Publishing setup and retries

Keep the existing `preview` environment and trusted publishers for
`Runic-Artifex/runic-sdk`, workflow `publish-preview.yml`, environment `preview`.
NuGet uses `NuGet/login` and `vars.NUGET_USER` (the profile username); npm publishes
through OIDC with the `preview` tag. Until 1.0, every release is a preview, so after the
GitHub release the workflow moves npm `latest` to it, never to an older version.
Each npm trusted publisher therefore also needs **Allow npm dist-tag** enabled.
The filename is retained so installed trusted-publisher registrations keep working.
A new npm package name may still need its trusted publisher configured; a new NuGet
package ID must be covered by the NuGet policy (see
[New NuGet package IDs](#new-nuget-package-ids)). That is account setup, not a
recurring release acceptance checklist.

If publication fails, rerun the failed `publish` job in the same run. If NuGet is still
indexing a previous push, wait for it to become available before retrying.
It reuses the same CI artifact, checks already published versions for matching
contents, and publishes only missing packages. Changed package contents require a new version. Registry indexing and public installation do not block GitHub release creation;
NuGet can take up to an hour to expose newly accepted packages, and the `indexed` job
reports when it has. If that job times out, rerun it. Assets upload to a draft before
it becomes public, so interrupted uploads can be resumed. An existing published
release for the same source is preserved on retry, so a rerun after a partial
publication finishes the remaining steps, including moving `latest`. CI package artifacts are kept for 30 days. If the artifact
expired, rerun all jobs of the CI run (which uploads it again), or prepare a new version.

After indexing, optional diagnostics can be run with `bun eng/release/cli.mjs
registry <manifest>` and `bun eng/release/smoke.mjs`. Download the manifest from
the run’s release diagnostics and use the released source checkout. These checks
do not republish packages.

The smoke installs a .NET library and the CLI tool, runs them, and installs/imports
the npm Views Svelte outlet outside the checkout. The wider template/framework
matrix already runs before publication. A smoke failure is actionable; it does not
trigger an additional manual matrix automatically.

For packaging/release-tool changes, use focused checks:

```sh
bun run test eng/release/contracts.test.mjs
bun run test eng/release/workflow.test.mjs
bun test eng/release/nuget-policy.test.mjs
bun test eng/release/github.test.mjs eng/release/sbom.test.mjs
bun run test eng/release/ci-run.test.mjs
bun test eng/release/template-locks.test.mjs
```

`cli.mjs prepare` records the package inventory and hashes for the current run;
`verify` checks the downloaded files; `publish` checks registry identity/content
before sending missing versions; `registry` checks availability; `wait-indexed` waits
until nuget.org lists the NuGet packages. These are internal
workflow steps, not files a maintainer must assemble by hand.

## New NuGet package IDs

NuGet packages are pushed with the short-lived key that `NuGet/login` gets from the
nuget.org trusted-publishing policy of the `runic-artifex` account. The key can push
only IDs that match one of the policy's package globs, a new ID only if the policy
may push new packages, and an existing ID only if `runic-artifex` owns it. Otherwise
nuget.org answers 403 during `dotnet nuget push`, after earlier packages of the
release may already be published (runic-translations-sdk 0.6.0-preview.6 stopped
that way).

[`nuget-policy.json`](nuget-policy.json) mirrors the policy: the owner, its package
globs (`*` matches any run of characters, case-insensitively, as on nuget.org) and
whether it may push new packages. `cli.mjs publish`, in the dry run and in the
`publish` job before the first push, checks every NuGet package that is not
published yet (`nuget-policy.mjs`): its ID must match a glob; a new ID (one nuget.org
does not know) needs `pushNewPackages`; an existing ID must list `runic-artifex`
among its owners. These lookups use the public service index, search and
flat-container endpoints and need no key. A failure names each package ID and the
remedy, and nothing is published. The check notes every new ID it lets through.
`nuget-policy.test.mjs` also fails when a NuGet package in `eng/workspace.json`
matches none of the mirrored globs.

Before releasing a new NuGet package ID:

1. Sign in to nuget.org as an administrator of `runic-artifex`, open
   **Trusted Publishing**, and edit the policy `Runic-Artifex-SDK` for
   `Runic-Artifex/runic-sdk`, `publish-preview.yml` and the `preview` environment:
   allow pushing new packages and make a package glob match the new ID. Since
   2026-10-12 the globs are `Runic.*` and `dotnet-runic*`, which cover every ID in
   `eng/workspace.json`; an ID outside them needs a new glob.
2. Update `nuget-policy.json` in the same pull request that adds the package, so it
   stays an exact copy of the policy.
3. Run **Publish preview** with **dry-run**; it reports the new ID with a notice.

The mirror is the only source the check has: nuget.org does not publish a policy,
and no request tells whether a key may push a version that does not exist yet.
Pushing a throwaway package to probe the key would itself publish, and a step after
`NuGet/login` could only try the real push. Keep the mirror in step when the policy
changes.

## After publication: docs catalog

The release workflow does not push to other repositories. After publication,
refresh the public portal from the `runic-site` repository with
`bun run docs:release <version>` (the publish run's summary repeats this).
This records the released tag's package catalog in one file. Commit it with any
API guide changes and deploy the docs; see [portal maintenance](https://github.com/Runic-Artifex/runic-site/blob/main/docs/README.md).
The marketing site links to the docs and needs no routine version edits.

## Versioning and support from 1.0

This policy takes effect with 1.0.0. Until then every release is a preview: any
release may change or remove an API, and only the latest preview gets fixes.
It covers the three SDKs, Runic SDK (this repository),
[Runic Command Line](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10)
and [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10),
whose release guides add only their own rules. The
[documentation portal](https://docs.runic-artifex.eu/support/policy/) states the
same policy.

### Version numbers

- Each SDK follows [Semantic Versioning 2.0](https://semver.org/). A major release
  may break; a minor release adds features, packages, platforms and deprecations;
  a patch release only fixes.
- All packages of one repository share its version (`eng/workspace.json` here).
- Previews of the next version, `x.y.0-preview.N`, remain possible. From 1.0 they
  go to the npm `preview` dist-tag, and npm `latest` moves only to stable
  versions; until 1.0 it moves to every preview (see
  [Publishing setup and retries](#publishing-setup-and-retries)).
- APIs marked `[Experimental]` and the preview package (see
  [Package tiers](#package-tiers)) are outside these rules.

### What counts as breaking

A change is breaking when a project that built and ran against a release fails to
build, fails to run or behaves differently against a later minor or patch release
of the same major without changes by its author. That includes:

- **C# API** of every library with a `PublicAPI.Shipped.txt`: removing or
  renaming a public type or member; changing a signature, return type,
  nullability or default parameter value; adding an abstract member, or an
  interface member without a default implementation; sealing a type; changing a
  positional record's primary constructor; changing documented behaviour or a
  documented failure result. New types, overloads that create no ambiguity,
  optional `init` properties and default interface members are not breaking.
- **NativeAOT and trimming:** a library marked `IsAotCompatible` reporting new
  trim or AOT warnings for code that was clean.
- **Diagnostic IDs** (`RUNICBRIDGE`, `RUNICPA`, `RUNICWPF`): reusing an ID for
  another meaning, or turning a warning into an error. New warnings are not
  breaking, nor are new errors for input that already failed at run time.
- **MSBuild properties, items and targets** that the documentation names:
  removing or renaming one, or changing a default so that a passing build fails
  or produces a different application. A renamed property keeps the old name
  working with a warning, as `RUNICBRIDGE017` does.
- **Command-line tools** (`dotnet runic`, `runic-create`): removing or renaming a
  command or option (a hidden alias stays), changing exit codes or a JSON
  contract. Human-readable output is not a contract.
- **Versioned JSON contracts** (`runic.create/1`, `runic.application.tool/1`,
  `runic.application.tool.doctor/1`, `runic.size/1`, `runic.support-envelope/1`,
  `runic.assets/1`, `runic.assets.archive/1`, `runic.assets.pack-result/1`):
  removing, renaming or retyping a field, changing property casing, or changing
  the meaning of a value. Such a change gets a new identifier (`/2`), and the old
  one stays available until the next major. New optional fields are not breaking;
  consumers ignore unknown fields.
- **Bridge protocol and generated clients:** a change that .NET and npm packages
  of the same release cannot negotiate. Mixing .NET and npm packages of different
  releases is unsupported; the exact `@runic-artifex/views` peer versions enforce it.
- **npm packages:** removing or renaming an export, `exports` entry or TypeScript
  type; narrowing a peer dependency range; changing the generated client shape or
  `runic-views.js` behaviour. Widening a peer range is not breaking.
- **Platforms:** dropping a supported RID, host or OS version before .NET drops it.
- **Third-party types in the public API** (ReactiveUI in the `*.ReactiveUI*`
  packages, `CsWebUi.WebUiWindow`): moving to a dependency version that changes a
  type Runic exposes.

### Enforcement

- Every shipping library tracks its API with `Microsoft.CodeAnalysis.PublicApiAnalyzers`
  and enables package validation against `RunicPackageValidationBaselineVersion`
  in `eng/Versions.props` (`eng/build/public-api.targets`). After each release the
  Unshipped entries move to Shipped and the baseline moves to the published
  version ([Routine preview release](#routine-preview-release), step 6).
- From 1.0 the baseline is the latest stable release of the major line, never a
  preview. A `CompatibilitySuppressions.xml` is allowed only in a major release,
  for `[Experimental]` members, or for a security fix that the release notes
  explain. A `*REMOVED*` entry in a `PublicAPI.Unshipped.txt` fails a minor or
  patch release unless the member is experimental.
- The versioned JSON contracts have schema snapshot tests, and documented MSBuild
  property names have tests that they still work or warn with their replacement.
- A stable package never depends on a prerelease package.

### How the three SDKs version

Runic Translations depends on Runic Command Line (`Runic.Translations.CommandLine`
and `Runic.Translations.Tooling` reference `Runic.CommandLine`). Runic SDK
libraries depend on neither; its `dotnet-runic` and `Runic.Create` tools bundle
Command Line.

- 1.0.0 ships as one release train in dependency order: Command Line, then
  Translations, then Runic SDK. "Runic 1.0" means that set.
- After 1.0 each SDK releases its minors and patches independently. Its release
  notes name the versions of the other SDKs it was tested with. References
  between SDKs use the lowest stable version that works as a NuGet minimum
  version, never an exact pin.

### Supported platforms

[`eng/support.json`](../support.json) is the source of the current status; the
[README table](../../README.md#supported-platforms) and
`dotnet runic doctor --rid <rid>` are generated from it. From 1.0 a host and RID
pair is supported only when Runic CI builds and runs that host's native layer on
it. "Packaged, not CI-verified" ships and is best effort, outside the promise;
"unsupported" means Runic ships no native assets for it.

During 0.9, CI is planned to cover `linux-arm64` and `win-arm64` on hosted Arm
runners and the musl RIDs in an Alpine container, which makes them supported
where the host ships native assets. The planned 1.0 status:

| RID | Runic Desktop | CS-WebUI (preview) | WPF |
| --- | --- | --- | --- |
| `linux-x64` | Supported | Supported | n/a |
| `win-x64` | Supported | Supported | Supported |
| `osx-arm64` | Supported | Supported | n/a |
| `linux-arm64` | Supported (planned for 0.9) | Supported (planned for 0.9) | n/a |
| `win-arm64` | Supported (planned for 0.9) | Unsupported | Supported (planned for 0.9) |
| `linux-musl-x64` | Supported (planned for 0.9) | Unsupported | n/a |
| `linux-musl-arm64` | Supported (planned for 0.9) | Unsupported | n/a |
| `osx-x64` | Packaged, best effort | Packaged, best effort | n/a |

- CS-WebUI ships no native library for musl or `win-arm64`. Publish for a glibc
  RID or `win-x64` (which runs under emulation on Windows on Arm), or use Runic
  Desktop.
- `Runic.Platform.Administration.Windows` supports Windows x64 only; arm64 may
  follow in a 1.x minor.
- Packages without native assets, such as `Runic.Navigation` and
  `Runic.Application.Testing`, are supported wherever .NET 10 is.

Minimum operating systems and components, from `eng/support.json`:

| OS | Hosts | Minimum |
| --- | --- | --- |
| Windows | Runic Desktop, CS-WebUI, WPF | Windows 11, or a Windows 10 release that .NET 10 supports; Microsoft Edge WebView2 Evergreen Runtime (CS-WebUI needs it only without a Chromium-family browser) |
| macOS | Runic Desktop, CS-WebUI | macOS 15 |
| Linux (glibc) | Runic Desktop | GTK 3 and WebKitGTK 4.1, or GTK 4.12 and WebKitGTK 6.0 with `Runic.Desktop.Gtk4` |
| Linux (glibc) | CS-WebUI | glibc 2.34; GTK 3 and WebKitGTK 4.1 when no Chromium-family browser is installed |
| Linux (musl) | Runic Desktop | The Alpine releases .NET 10 supports, with GTK 3 and WebKitGTK 4.1 (planned for 0.9) |

Runic supports the Linux distributions that .NET 10 supports and that provide
these libraries.

### .NET versions

Every package targets `net10.0` (WPF packages `net10.0-windows`). .NET 10 is an
LTS release that Microsoft supports until November 2028. Applications on a later
.NET in support use the `net10.0` assets. Adding a target framework is a minor
release. Dropping a .NET version needs a major release, and `net10.0` is not
dropped before .NET 10 leaves support. The SDK pinned in `global.json` is a contributor
requirement, not a consumer one.

### Package tiers

- **Stable:** every shipped package from 1.0.0, under the rules above.
- **Experimental APIs in stable packages:** an unfinished API is marked
  `[Experimental]` with one diagnostic ID per API family, listed with its status
  in [`docs/experimental.md`](../../docs/experimental.md). Every use is a compile
  error until the project suppresses the ID, and the API may change or be removed
  in a minor release. An experimental type does not appear in a stable API's
  signature. Graduation removes the attribute. Today's only family,
  `RUNICWPF001` (WPF child web presentation), is planned to graduate before 1.0.
- **Preview:** `Runic.Application.Views.CsWebUi` is the only preview package. It
  depends on the prerelease `CsWebUi`, so it ships with each stable release on a
  prerelease version and may change in any release.

### Deprecation

- A public API is deprecated with `[Obsolete]`, a diagnostic ID and a message that
  names the replacement. It stays for at least one minor release and is removed
  only in a major release.
- A renamed MSBuild property keeps its old name working with a warning until the
  next major; a renamed command-line option keeps a hidden alias; a replaced JSON
  contract keeps its old identifier until the next major; an npm API gets a
  `@deprecated` JSDoc tag.
- Each deprecation has a release-notes entry with the migration.

### Fixes and support

- Bug and security fixes ship in a patch of the latest minor release only. Update
  to the latest minor to get them.
- A preview gets no fixes once the next preview or stable release ships.
- Operating system, WebView2, WebKitGTK and .NET runtime fixes come from their own
  servicing. Runic rebuilds for a .NET patch only where it ships the runtime.
- Report vulnerabilities privately as [`SECURITY.md`](../../SECURITY.md) describes.
