# Releasing Runic SDK

This is the SDK's current release policy. It replaces the first-preview acceptance
plans and the SDK portions of the organization launch/release runbooks. Historical
releases and their evidence remain historical records.

## Routine preview release

1. Commit the release's user-facing changes in the release pull request: write the
   introduction and highlights (`_intro.md` and `highlights/` in
   `eng/release/notes/<version>/`), run `bun eng/release/notes.mjs assemble <version>`,
   which writes `eng/release/notes/<version>.md` from the fragments and removes them
   (see [Release notes](#release-notes)), review the assembled notes, and commit them
   with the [migration guide](../../docs/migration-1.0.md). Do the
   [rechecks](#rechecks-before-each-release) and record their results in the pull
   request's description.
   `main` already carries the version, set after the previous release (step 6);
   package versions must agree with `eng/workspace.json`, and published versions
   cannot be overwritten.
2. Wait for a `ci.yml` run of that commit on `main` to succeed with every Windows and
   macOS job. A push runs only the Windows and macOS legs its changes need, so if it
   skipped one, run **SDK CI** on `main` manually (`gh workflow run ci.yml --ref main`)
   or wait for the nightly run, while that commit is still the head of `main`.
3. Optionally run **Publish preview** (`publish-preview.yml`) on `main` with
   **dry-run** checked. It performs every check below and writes nothing.
4. Run **Publish preview** on `main`, entering that version.
5. The workflow finds the successful CI run of the dispatched commit, downloads
   its `runic-sdk-<run id>` package artifact, verifies the inventory against the
   requested version and commit, attests their build provenance and SBOM, publishes
   those exact packages, creates the GitHub release with generated change notes, a
   package bundle, a CycloneDX SBOM and one checksum file, and moves npm `latest`
   when the version may have it (see [npm and GitHub release channels](#npm-and-github-release-channels)).
   The release is a GitHub prerelease for `0.y.z` and every prerelease version
   (`1.0.0-rc.N`); a stable version from `1.0.0` is a full release.
6. Wait for the run's `indexed` job: it ends once nuget.org lists every NuGet package
   of the release, which can take up to an hour after the push. Until then a restore of
   the new version fails with NU1102, so push the post-release commit after it. Then
   move each library's `PublicAPI.Unshipped.txt` entries into
   `PublicAPI.Shipped.txt`. A `*REMOVED*` entry is not moved: delete it together
   with the Shipped line it names. Then set `RunicPackageValidationBaselineVersion`
   in `eng/Versions.props` to the published version (from 1.0 only to a stable
   version: after `1.0.0-rc.N` it stays at the last stable release, see
   [Release candidates and 1.0.0](#release-candidates-and-100)), and
   `RunicCsWebUiPackageValidationBaselineVersion` to the version
   `Runic.Application.Views.CsWebUi` was published as, then delete the
   `CompatibilitySuppressions.xml` files, which describe breaks from the old baseline.
   Remove `<RunicPackageValidationBaselineMissing>` and `<RunicPackageFirstReleaseVersion>`
   from each package published for the first time (for example `Runic.Navigation` after
   0.7.0-preview.4), so its next release is validated against this one; the engineering
   tests fail until you do. Likewise remove `<PackageValidationBaselineName>` from a
   package published for the first time under a new ID (the `Runic.Application.Views*`
   packages after 0.7.0-preview.7), which until then validates against its old ID.
   Also remove each RS0026 `SuppressMessage` attribute whose overload's line is now in
   `PublicAPI.Shipped.txt`: the analyzer accepts shipped overloads. In 0.15 that is
   `WindowsDomainClient.DiscoverControllerAsync` with a `DomainControllerNameFormat`
   (`Runic.Platform.Administration.Windows`). `git grep -n RS0026 -- '*.cs'` lists the
   attributes that remain.
   In the same post-release work, move `main` to the next candidate: after `0.y.z`
   it is the next minor, `bun run version:bump 0.(y+1).0`, which advances
   `Runic.Application.Views.CsWebUi` to `0.(y+1).0-preview.1`, as Command Line and
   Translations do after their releases (D063.6, D066.13). Add
   `eng/release/notes/0.(y+1).0/_intro.md` with its title and install lines; changes
   add their notes beside it as fragments (see [Release notes](#release-notes)).
   The candidate stays until a pull request schedules the first
   release candidate (see [Release candidates and 1.0.0](#release-candidates-and-100)).
   Install instructions in the guides keep naming the published release, or no
   version, until the candidate is published.
7. Refresh the docs catalog (see the end of this page).

Full CI includes package/template consumers and native JIT/NativeAOT checks. The
release workflow does not run it again; it fails before publishing if the commit has
no successful CI run on `main`, if that run is still in progress, if it did not pass
every Windows and macOS leg, or if its package artifact has expired.

The Windows and macOS legs are `Native / win-x64`, `win-arm64` and `osx-arm64`,
`WPF navigation / win-x64` and `win-arm64`, and `Packaging / windows` and `macos`
(`releaseJobs` in `eng/ci/platforms.mjs`). This keeps the guarantee releases had when
every push to `main` ran all of them; it adds no check. Pushes now plan those legs from
their changes (eng/ci/README.md), so `ci-run.mjs` accepts a push, scheduled or manual
run of the exact commit and takes the newest successful one that ran them all; its
error names the legs a run skipped and asks for a manual run on `main`. There is no separate acceptance workflow,
receipt upload, candidate approval dossier, mandatory soak, or independent pilot
prerequisite. Manual checks are appropriate when a change affects native/UI
behavior that automation cannot cover. Soaks and matched benchmarks remain useful
for relevant changes and regression investigations; they are not routine gates.

Keep release notes focused on changes, installation, migration and meaningful
known issues. Put logs and internal package metadata in Actions artifacts. Record
nonblocking limitations as issues rather than introducing custom waiver formats.

## Rechecks before each release

Some code relies on another project's internals or carries a workaround until an
upstream release ships. Recheck each one in every release pull request (also for an
rc) and record the result in its description. Change the code, or the version it
accepts, only when the check says so.

| What | Where | Check | Then |
| --- | --- | --- | --- |
| Angular component mode replaces Angular's private `ɵSHARED_STYLES_HOST` | `packages/web/angular/src/component.ts` | For the Angular versions the `@runic-artifex/angular` peer range admits, and for a new major before widening it: the token is still exported with the same interface; Angular's bundles still have no `adoptedStyleSheets` support; the status of angular/angular#59429 and #68691; the component-mode browser test passes. | If Angular gains a public way, move to it. If the token changes, adapt it or use the public-API `RendererFactory2` fallback in local-planning's `design/dx-0.21/ANGULAR-SHADOW-STYLES.md`. Component mode stays experimental while it uses the token. |
| The `--angular-build vite` starter overrides `vite` | the `runic-app` `angular-vite` frontend and its `--translations` variant (`overrides`), the Angular frontend's `pnpm-workspace.yaml`, which only those projects receive | Whether the variant still uses `@angular/build` 22.2.4, which pins Vite 8.3.0; 22.2.5 and later depend on Vite 8.3.5, with the GHSA-587h-7wmj-h5pm fix (D072.3 keeps the variant as it is for now). | When the variant moves to such an `@angular/build`, remove its overrides and the `pnpm-workspace.yaml`, and regenerate the starter locks (`eng/dependencies/README.md`, Angular 22.2.7 row). |
| WebKitGTK's HTTP inspector accepts any origin (WebKit bug 330602) | the version gate in `tools/dotnet-runic/WebKit/WebKitDebugging.cs` | Which WebKitGTK release contains the fix. The gate assumes 2.54.3. | Correct the gate and the docs' version; the HTTP inspector then opens by default where WebKit allows it (D066.38). |
| `deepmerge` in the starter locks has GHSA-gcqx-w48m-w2h4 (low) and no fixed release | the starters' lock files | Whether a fixed `deepmerge` is published (and at least a day old, D038). | Update the starter locks (`bun eng/dependencies/update-template-locks.mjs`). |

Add a row when new code starts relying on internals or on a pending upstream fix, and
remove it when the dependency is gone. The tracking issue of such a check closes once
its row is here.

## Release notes

Each pull request writes its release notes as files of its own under
`eng/release/notes/<version>/`, where `<version>` is the candidate on `main`
(D066.33). Pull requests then never edit the same file, so merging one does not
make the others conflict and run CI again. The release pull request assembles the
fragments into `eng/release/notes/<version>.md`, the file that the publish
workflow passes to the GitHub release and that the migration guide links to.

One directory per section, one file per entry:

| Path | Assembled as |
| --- | --- |
| `_intro.md` | The title, install lines and introduction, before the first section. The release pull request writes the introduction. |
| `highlights/` | `## Highlights`, a list. Usually written by the release pull request. |
| `upgrading/` | `## Upgrading`: list items directly under it. Its `_intro.md` holds the heading with the previous version (`## Upgrading from 0.19`) and the text before the subsections. |
| `breaking/` | `### Breaking changes` under Upgrading, a list. |
| `behaviour/` | `### Behaviour and output changes` under Upgrading, a list. |
| `experimental/` | `### Experimental features and how to opt in` under Upgrading: list items or table rows. |
| `features/` | One `## Title (#issue)` section per file, after Upgrading. |
| `fixes/` | `## Fixes`, a list. |
| `ci/` | `## CI and tests`, a list. |
| `known-issues/` | `## Known issues`, a list. |

- **Names.** Name an entry by its issue or pull request: `features/951.md`,
  `fixes/965.md`; add words when one number has several entries in a section
  (`fixes/965-owned-windows.md`), and use words alone for an entry without one
  (`known-issues/side-panel-on-wayland.md`). Lower case, `.md`.
- **Content.** A feature fragment starts with its `##` heading and has no other
  `#` or `##` heading (`###` and deeper are fine). Every other entry is a list item
  (`- ...`, with indented continuation lines) or, in `experimental/`, a table row;
  it has no heading. One pull request contributes to as many sections as it needs:
  `features/951.md`, `breaking/951.md` and `fixes/951.md` side by side.
- **Section text.** A section's `_intro.md` comes after its heading and before its
  entries (a table header for the rows, for example), and `_end.md` after them. An
  `_intro.md` that starts with a heading of the section's level replaces the
  default heading.
- **Order.** Sections follow the table. Within one, entries with an `order: <n>`
  front matter come first, by it; the others follow by issue number, then by file
  name:

  ```md
  ---
  order: 10
  ---
  ## Side panel (#864, experimental)
  ```

- **Links.** Write links as they work from the assembled
  `eng/release/notes/<version>.md`: same-document anchors (`#fixes`,
  `#side-panel-864-experimental`) and links to the release tag, as before.

`eng/release/notes.mjs` has three commands:

- `bun eng/release/notes.mjs check [<version>...]` validates the fragments of the
  given versions, or of every fragment directory: known sections and file names,
  one `##` heading per feature fragment, list entries without headings, no
  repeated `##` heading, relative links and same-document anchors that resolve in
  the assembled notes, and no `<version>.md` beside the fragments. The engineering
  tests (`eng/release/notes.test.mjs`) run it in CI.
- `bun eng/release/notes.mjs assemble <version>` checks the fragments, writes
  `<version>.md` and removes `<version>/`, so a released version has one notes file,
  as before. Fragments added after the release belong to the next version.
- `bun eng/release/notes.mjs split <version> [<file>] [--keep]` turns notes written
  as one file into fragments: `<version>.md` (which it then removes, unless
  `--keep`), or `<file>`, for example a pull request branch's version of it. It
  skips what is a fragment already: the intro and a section's `_intro.md` or
  `_end.md` when present, a feature with the same heading, an entry with the same
  text. It reports an entry that differs from an existing one with the same issue
  number, which then needs a look. A pull request that still edits `<version>.md`
  converts its note when it merges `main`, without rename detection (Git can
  otherwise take the deleted file for a renamed fragment and merge the branch's
  text into that fragment):

  ```sh
  git merge -X no-renames origin/main   # stops: <version>.md modified here, deleted on main
  bun eng/release/notes.mjs split <version>   # adds the branch's own entries, removes the file
  git add eng/release/notes && git commit --no-edit
  ```

`publish-preview.yml` stops before it creates the GitHub release, also in a dry
run, while `eng/release/notes/<version>/` still holds fragments. When `main`'s
version changes before a release (the pull request that schedules `1.0.0-rc.1`),
rename the directory with `git mv`.

## Release trains: the compatibility gate

When Runic Command Line, Runic Translations and Runic SDK release together (a train,
D060; order Command Line, Translations, SDK), the **Train compatibility** workflow
(`train-compat.yml`, [eng/train/README.md](../train/README.md)) restores, builds and
runs consumers of the three repositories' candidate packages together with warnings
as errors: a German Command Line app with NativeAOT, the adapter's key coverage, both
template apps with Translations, Hosting, Views.Translations and a Command Line front
end, the tools, WPF, the Translations editor (informational) and the version ranges
and pins of every package. Each repository's input is a published version or the
package artifact of a CI run.

1. When the three release changes are on `main` and their CI runs passed, run row
   **T** against the Command Line CI run that will be published:
   `gh workflow run train-compat.yml -f row=T -f cli=run:<id> -f translations=run -f sdk=run`.
   It must pass before Command Line publishes.
2. After Command Line is published, Translations refreshes its lock files and runs
   row **P-tr** (`cli=published:<version>`, `translations=run:<id>`, `sdk=run`)
   before it publishes.
3. After Translations is published, refresh runic-sdk's lock files against nuget.org
   (eng/train/README.md, "Train feed") and merge that. Publish preview then runs row
   **P-sdk** (`cli=published:<version>`, `translations=published:<version>`,
   `sdk=run:<the CI run it publishes>`) in its `train` job, and publishes only after
   it passes.

Each repository's `publish-preview.yml` calls the gate as a reusable workflow in a
`train` job that its publishing job needs (eng/train/README.md, "Publish workflows"),
also in a dry run. A run lists every package with its SHA-256 in `train-result.json`,
so the packages it checked can be compared with the ones published. The nightly rows
(the newest published versions, and the newest `main` runs) only report.

`eng/train/expected-failures.json` names the incompatibilities of published packages
that a train fixes; each entry applies only below the fixing version, and the release
that fixes it removes it. It is empty since the 0.18 train's entries (Translations
0.9.x) went with the publication of Translations 0.10.0.

## Workflow jobs and dry run

- `preflight` (`contents: read`, `actions: read`) checks the requested version and
  runs `eng/release/ci-run.mjs` to find the CI run and the id of its package artifact,
  and reads the train's Command Line and Translations versions for `train`.
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
- `train` (`contents: read`, `actions: read`) calls `./.github/workflows/train-compat.yml`
  with row **P-sdk** after `candidate`: the preflight CI run's packages
  (`sdk=run:<id>`) with the Command Line and Translations versions of
  `Directory.Packages.props` as published on nuget.org. `publish` needs it.
- `indexed` (`contents: read`, no environment) runs after `publish` and polls
  `cli.mjs wait-indexed`: every 30 seconds, for up to an hour, it reads each NuGet
  package's flat-container version list, which `dotnet restore` uses, until all list
  the release's version. The GitHub release and npm `latest` do not wait for it.

A dry run therefore ends after `candidate` and `train`, with no OIDC token, attestation,
registry write, tag or release. A failing dry-run check is the failure the real
run would hit. `eng/release/workflow.test.mjs`, `github.test.mjs`, `sbom.test.mjs`
and `ci-run.test.mjs` pin this contract.

Publications run one at a time. Every workflow that publishes waits in the one
`publish-preview` concurrency group, whatever the version, and never cancels a
publication in progress; `workflow.test.mjs` checks it.

## npm archive source commit

A first-party npm archive's `package.json` `gitHead` is the commit that **last
changed the package's inputs**, not the commit the build ran on (D069.2). Bun packs
with fixed mtimes, modes and entry order, and the stamped archive is re-gzipped with
a zero mtime and no host operating system in its header, so the archive bytes depend
only on those inputs. A commit outside them (a workflow, a .NET package, a document)
leaves every npm archive and its integrity unchanged; that keeps committed template
locks and the Translations train candidates' hashes valid across unrelated commits.

`eng/release/npm-inputs.mjs` defines the inputs of each package:

- its directory, and the directories of the first-party packages it depends on,
  transitively (their versions and declaration files reach its archive);
- its extra inputs: for `@runic-artifex/views-translations`, the catalog of
  `Runic.Application.Views.Translations` and `NuGet.config`;
- the shared build inputs: the workspace `package.json` and `bun.lock`, the
  toolchain (`flake.nix`, `flake.lock`, `nix/toolchain`, `.node-version`,
  `global.json`, `.config/dotnet-tools.json`), `eng/typescript`, `eng/web`,
  `eng/workspace.json`, `eng/run.mjs`, `eng/version.mjs`,
  `eng/node-compatibility.mjs`, the packing code (`eng/release/pack-npm.mjs`,
  `readme-links.mjs`, `artifacts.mjs`, `npm-inputs.mjs`) and `.gitattributes`.

The set is deliberately wide: a missing input would let two different archives
share a `gitHead`. A package that starts reading a file outside these paths adds it
to the module; `npm-inputs.test.mjs` checks that tsconfig bases and build-script
imports outside a package directory are covered. Packing computes the commit with
`git log -1 -- <inputs>`, so it needs the full history (the packing and release
jobs check out with `fetch-depth: 0`), and CI refuses to pack a package whose inputs
have uncommitted changes.

The release check (`artifacts.mjs` `scan`, run by `cli.mjs prepare`, `verify` and
`publish` and after packing) accepts an npm archive whose `gitHead` is the release
commit or an ancestor of it with the same inputs (`git diff` between the two is empty
for them); it refuses any other commit. NuGet packages still name the release commit
itself. In the SBOM, an npm component's `runic:source-commit` is this `gitHead`; the
document's own `runic:source-commit` and npm's provenance attestation name the
release commit.

## Verifying a release

Every package and release asset has a signed build-provenance attestation from
`publish-preview.yml` on `main`, and the packages and bundle have an SBOM
attestation. Verify with the GitHub CLI (`gh auth login` first):

```sh
version=0.7.0-preview.1
gh release download "v$version" -R Runic-Artifex/runic-sdk
gh attestation verify "runic-sdk-$version-packages.tar.gz" -R Runic-Artifex/runic-sdk \
  --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml
tar -xzf "runic-sdk-$version-packages.tar.gz"
gh attestation verify "nuget/Runic.Application.$version.nupkg" -R Runic-Artifex/runic-sdk \
  --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml
# The SBOM attestation (CycloneDX); the release asset runic-sdk-<version>.cdx.json is the same document.
gh attestation verify "nuget/Runic.Application.$version.nupkg" -R Runic-Artifex/runic-sdk \
  --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml \
  --predicate-type https://cyclonedx.org/bom
# npm serves the published bytes unchanged, so a registry tarball verifies directly.
npm pack "@runic-artifex/views@$version"
gh attestation verify "runic-artifex-views-$version.tgz" -R Runic-Artifex/runic-sdk \
  --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml
```

Pass `--signer-workflow` for every file. The provenance names `publish-preview.yml`,
the workflow that attested and published the bytes, not the job that built them.
The packages are built by the `build` job of a successful `ci.yml` run for the
same commit, where the locked build and test dependencies also run. The publish
workflow accepts only that run's artifact, by id, and checks the files against
the candidate's hashes and inventory before it attests them (see
[Workflow jobs and dry run](#workflow-jobs-and-dry-run)).

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
through OIDC with the `preview` tag, and after the GitHub release the workflow may move
npm `latest` (see [npm and GitHub release channels](#npm-and-github-release-channels)).
Each npm trusted publisher therefore also needs **Allow npm dist-tag** enabled.
The filename is retained so installed trusted-publisher registrations keep working;
1.0.0 and later stable releases are published by the same `publish-preview.yml`.
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
bun test eng/version.test.mjs
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

## npm and GitHub release channels

The version alone decides where a release goes (`latestEligible` in `registry.mjs`,
`githubPrerelease` in `tags.mjs`; `workflow.test.mjs` and `github.test.mjs` pin them):

| Version | Example | npm `preview` | npm `latest` | GitHub release |
| --- | --- | --- | --- | --- |
| `0.y.z` (any 0.x version) | `0.11.0` | yes | moves to it if higher | prerelease |
| prerelease from 1.0 | `1.0.0-rc.1`, `1.1.0-preview.1` | yes | unchanged | prerelease |
| stable from 1.0 | `1.0.0`, `1.0.1` | yes | moves to it if higher | release (not a prerelease) |

Every npm package is published with `--tag preview`, so `preview` always names the
newest publication, and `tag-latest` then moves `latest` only to an eligible, higher
version, never backwards on a late rerun. During the release candidates npm `latest`
therefore stays at 0.11.0 until 1.0.0 is published (D043.5), as NuGet clients skip
prereleases by default. The **Check requested version** step accepts any SemVer
version without build metadata that `eng/workspace.json` commits: `0.y.z`, a
prerelease such as `1.0.0-rc.1`, or a stable `x.y.z`.

## Release candidates and 1.0.0

1.0.0 follows `1.0.0-rc.N` (D026.5, D028.6). Each rc, and 1.0.0 itself, is a routine
release with these differences:

1. `bun run version:bump 1.0.0-rc.N` (then `1.0.0`): the pull request that schedules
   rc.1 replaces the `0.(y+1).0` candidate on `main`, and the release pull request
   of each later rc, and of 1.0.0, sets its version. `Runic.Application.Views.CsWebUi`
   stays a prerelease with its own counter (D043.6): `1.0.0-preview.N` would sort
   below `1.0.0-rc.N`, so it keeps the `preview` label throughout. Its version is
   always `<train major.minor.patch>-preview.N` (`bun eng/version.mjs check` enforces
   it), and `version:bump` advances it to the next `-preview.N` of the new train
   version, starting at `-preview.1` on a new major.minor.patch:

   | Release train | `Runic.Application.Views.CsWebUi` |
   | --- | --- |
   | 0.11.0 | 0.11.0-preview.1 |
   | 1.0.0-rc.1 | 1.0.0-preview.1 |
   | 1.0.0-rc.2 | 1.0.0-preview.2 |
   | 1.0.0 | 1.0.0-preview.3 (one more than the last rc) |
   | 1.0.1 | 1.0.1-preview.1 |

   `--cswebui <version>` sets another `-preview.N` of the same major.minor.patch, for
   example to skip a number that was published by mistake.
2. Package validation runs without `CompatibilitySuppressions.xml`: an rc takes no API
   or behaviour changes. In the dry run of an rc, `tag-latest` reports
   `1.0.0-rc.N is a prerelease: npm latest stays where it is` and `github.mjs release`
   `Would create the prerelease`; for 1.0.0 they report
   `Would move <package> latest from 0.11.0 to 1.0.0` and `Would create the release`.
3. After each rc, move any `PublicAPI.Unshipped.txt` entries to Shipped as in step 6
   (only rc.1 may have some), but leave `RunicPackageValidationBaselineVersion` at the
   last stable release, 0.11.0: from 1.0 the baseline is never a prerelease, and
   `bun eng/version.mjs check` fails once the train is 1.0 or later and the baseline
   has a prerelease suffix. `RunicCsWebUiPackageValidationBaselineVersion` moves to
   the published CsWebUi version as before.
4. After 1.0.0 (the train Command Line → Translations → Runic SDK has finished):
   - set `RunicPackageValidationBaselineVersion` to `1.0.0` and
     `RunicCsWebUiPackageValidationBaselineVersion` to the published CsWebUi version;
   - every `PublicAPI.Unshipped.txt` is empty, its entries in `PublicAPI.Shipped.txt`;
   - delete every `CompatibilitySuppressions.xml`: from now on one is allowed only as
     [Enforcement](#enforcement) describes;
   - remove the pre-1.0 wording: "Until 1.0" in [`SECURITY.md`](../../SECURITY.md) and
     the "until 1.0" / "Until then" sentences of this guide
     ([Versioning and support from 1.0](#versioning-and-support-from-10),
     [Version numbers](#version-numbers) and the `0.y.z` row of
     [npm and GitHub release channels](#npm-and-github-release-channels)), keeping
     the rules that hold from 1.0;
   - refresh the docs catalog and deploy the site with the maintainer's approval.

## After publication: docs catalog

The release workflow does not push to other repositories. After publication,
refresh the public portal from the `runic-site` repository with
`bun run docs:release <version>` (the publish run's summary repeats this).
This records the released tag's package catalog in one file. Commit it with any
API guide changes and deploy the docs; see [portal maintenance](https://github.com/Runic-Artifex/runic-site/blob/main/docs/README.md).
The marketing site links to the docs and needs no routine version edits.

## Versioning and support from 1.0

This policy takes effect with 1.0.0. Until then every release is a preview with
a plain `0.y.z` version: a new minor (`0.9.0` to `0.10.0`) may change or remove
APIs, a patch (`0.9.1`) only fixes or adds, and only the latest minor gets fixes.
Releases from 0.9.0 on carry no `-preview.N` suffix; GitHub still marks them as
prereleases. `Runic.Application.Views.CsWebUi` keeps a prerelease version of its
own while the CS-WebUI release it depends on is a prerelease.
It covers the three SDKs, Runic SDK (this repository),
[Runic Command Line](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10)
and [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10),
whose release guides add only their own rules. The
[documentation portal](https://docs.runic-artifex.eu/support/policy/) states the
same policy.

Applications on 0.9 follow the [migration guide to 1.0](../../docs/migration-1.0.md),
which collects the breaking and behaviour changes of 0.10.0, 0.11.0, 0.12.0, 0.13.0, 0.14.0, 0.15.0, 0.16.0, 0.17.0, 0.18.0, 0.19.0, 0.20.0 and 0.21.0.

### Version numbers

- Each SDK follows [Semantic Versioning 2.0](https://semver.org/). A major release
  may break; a minor release adds features, packages, platforms and deprecations;
  a patch release only fixes.
- All packages of one repository share its version (`eng/workspace.json` here).
- From 1.0, previews of the next version, `x.y.0-preview.N` or `x.y.0-rc.N`, remain
  possible, starting with `1.0.0-rc.N`. They go to the npm `preview` dist-tag, and npm
  `latest` moves only to stable versions; until 1.0 it moves to every `0.y.z` release
  (see [npm and GitHub release channels](#npm-and-github-release-channels)).
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
  A positional record therefore keeps its primary constructor and `Deconstruct`
  for the major and gains data only as optional `init` properties; this applies
  to records added later too.
- **Enums and string unions:** the documentation of every public enum and
  exported npm string union says whether it is open or closed. An open set, such
  as failure codes, rejection reasons, error kinds and diagnostic codes, may gain
  values in a minor release: code that switches over it handles an unknown value,
  for example with a default arm, and a TypeScript exhaustive `never` check over
  it can stop compiling. A closed set, such as `DesktopFileOperation`, gains
  values only in a major release. Removing, renaming or renumbering a value is
  breaking for both. Multi-word wire values (Bridge protocol tokens and npm string
  unions) are kebab-case, such as `domain-failed` and `timed-out`.
- **NativeAOT and trimming:** a library marked `IsAotCompatible` reporting new
  trim or AOT warnings for code that was clean.
- **Diagnostic IDs** of every family below: reusing an ID for another meaning, or
  turning a warning into an error. New warnings are not breaking, nor are new
  errors for input that already failed at run time. Message text is not a
  contract. [`docs/diagnostics.md`](../../docs/diagnostics.md) describes every ID,
  and [`docs/experimental.md`](../../docs/experimental.md) the experimental ones,
  which number from `100` in a family that also reports diagnostics.

  | Prefix | Reported by |
  | --- | --- |
  | `RUNICBRIDGE` | The Runic.Application.Views generator, its analyzers (since 0.20) and build targets; its runtime reports `RUNICBRIDGE038` (since 0.19) |
  | `RUNICPA` | Privileged actions: the build targets of Runic.Platform and its platform packages |
  | `RUNICEL` | The Runic.Platform.Windows.Elevation build targets |
  | `RUNICDBUS` | The Runic.Platform.Linux.DBus source generator; experimental D-Bus APIs |
  | `RUNICDESK` | The Runic.Desktop build targets; experimental Desktop APIs |
  | `RUNICPLAT` | The Runic.Platform analyzers (since 0.14) |
  | `RUNICASSETS` | Experimental APIs of Runic.Assets |
  | `RUNICNAV` | The Runic.Navigation.ReactiveUI build targets (since 0.11); `RUNICNAV001`, the graduated experimental Navigation ID, is retired |
  | `RUNICWPF` | Retired: graduated experimental family of Runic.Application.Views.Wpf |
  | `RUNICP` | `@runic-artifex/vite-plugin-runic` |
  | `RAPPCLI`, `RAPPDEV`, `RAPPPKG`, `RAPPSIZE` | `dotnet runic` (`RAPPPKG` since 0.11) |
  | `RCLI` | The Runic.CommandLine codes the tools report, and `dotnet runic doctor` checks (`RCLI81xx`) |
  | `RCREATE` | `runic-create` |
  | `RAS` | The asset packer and the Runic.Assets build targets |
- **MSBuild properties, items and targets** that the documentation names:
  removing or renaming one, or changing a default so that a passing build fails
  or produces a different application. A renamed property keeps the old name
  working with a warning, as `RUNICBRIDGE017` does.
- **Command-line tools** (`dotnet runic`, `runic-create`): removing or renaming a
  command or option (a hidden alias stays), changing exit codes or a JSON
  contract. Human-readable output is not a contract.
- **Versioned JSON contracts** (`runic.create/2`, `runic.application.tool.dev/1`,
  `runic.application.tool.size/1`, `runic.application.tool.doctor/1`,
  `runic.application.tool.package/1`,
  `runic.size/2`, `runic.assets/1`, `runic.assets.archive/1` and `/2`,
  `runic.assets.pack-result/1`; support envelopes belong to
  [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10)):
  removing, renaming or retyping a field, changing property casing, or changing
  the meaning of a value. Such a change gets a new identifier (`/2`), and the old
  one stays available until the next major. New optional fields are not breaking;
  consumers ignore unknown fields.
- **Bridge protocol and generated clients:** a change that .NET and npm packages
  of the same release cannot negotiate. Mixing .NET and npm packages of different
  releases is unsupported; the exact `@runic-artifex/views` peer versions enforce it.
  The registered `Symbol.for` keys, the `__runicBridge` global and the diagnostics
  channel, which copies of the npm packages on one page share, are part of the
  protocol ([specification](../../specs/application/README.md#shared-globals)).
- **Templates** (`dotnet new runic-app` and the `runic-create` choices that
  select it): the template identity and short name, parameter names, choice
  values and defaults. Removing a parameter or choice, or changing a default, is
  breaking. The content a template generates is not a contract: a later release
  may generate different code, and an application created from an earlier
  release keeps building under the rules above.
- **Logging and telemetry:** log event IDs and names, `ActivitySource` and `Meter`
  names, and the instrument and tag names that the documentation names stay
  stable within a major. Log message text is not a contract.
- **npm packages:** removing or renaming an export, `exports` entry or TypeScript
  type; narrowing a peer dependency range; changing the generated client shape or
  `runic-views.js` behaviour. Widening a peer range is not breaking. The
  `./experimental` subpaths, and the `./generated*` and `./bindings` entries of
  `@runic-artifex/views` (see Package tiers), are excepted.
- **Platforms:** dropping a supported RID, host or OS version before .NET drops it.
- **Third-party types in the public API:** moving to a dependency version that
  changes a type Runic exposes (see [Dependencies](#dependencies)).

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
Command Line, from 1.0 a stable release of it.

- 1.0.0 ships as one release train in dependency order: Command Line, then
  Translations, then Runic SDK. "Runic 1.0" means that set.
- After 1.0 each SDK releases its minors and patches independently. Its release
  notes name the versions of the other SDKs it was tested with. References
  between SDKs use the lowest stable version that works as a NuGet minimum
  version, never an exact pin.

### Dependencies

Between packages of this repository:

- Every package references another package of the repository with the exact
  version of the release (`[x.y.z]`), which `eng/build/package-dependencies.targets`
  writes into the packages, and each npm adapter requires the exact
  `@runic-artifex/views` version. A package that uses another package's internals
  through `InternalsVisibleTo` must keep that exact reference: internals are not
  API and change in any release, so both packages have to come from the same
  release. Where the consumer needs an API that applications should use too, make
  it public instead.

Third-party packages:

- A package whose types appear in a shipped API is referenced directly and
  bounded to the major Runic is tested with (`Directory.Packages.props`;
  `eng/workspace.test.mjs` checks it), so a restore cannot combine Runic with an
  untested major:

  | Package | Range | Exposed by |
  | --- | --- | --- |
  | `ReactiveUI`, `ReactiveUI.Core`, `ReactiveUI.Reactive` | `[26.0.1, 27)` | The `*.ReactiveUI*` packages |
  | `ReactiveUI.Binding`, `ReactiveUI.Binding.Reactive` | `[9.1.0, 10)` | The `*.ReactiveUI*` packages |
  | `ReactiveUI.Primitives`, `ReactiveUI.Primitives.Reactive`, `ReactiveUI.Disposables` | `[9.0.0, 10)` | The `*.ReactiveUI*` packages |
  | `System.Reactive` | `[7.0.0, 8)` | The `*.ReactiveUI.Reactive` packages |
  | `CsWebUi` (`WebUiWindow`) | `[2.5.0-beta.4.9, 3)` | `Runic.Application.Views.CsWebUi` (preview) |
  | `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) | `[10.10.0, 11)` | `Runic.Application.Testing` |
  | `effect` (npm) | `^4.0.1` | `@runic-artifex/views-effect` |
  | `Runic.Translations`, `Runic.Translations.Hosting` | `[0.14.0, 0.15.0-0.0)` | `Runic.Application.Views.Translations` |

- Runic Translations releases on its own timeline. Before its 1.0 a minor release
  may break, so `Runic.Application.Views.Translations` is bounded to the
  Translations minor it is built with, the range `Runic.Translations.Build` gives
  any library; the `-0.0` bound keeps the next minor's previews out as well.
- Moving to a new major of one of them needs a major Runic release, with one
  exception: the ReactiveUI adapters (`Runic.Application.Views.ReactiveUI*` and
  `Runic.Navigation.ReactiveUI*`, both flavors together) may move to a new
  ReactiveUI major in a minor release, with a migration entry in its release notes.
- The `Microsoft.Extensions.*` abstractions, such as dependency injection and
  logging, follow .NET's own compatibility and are not bounded.
- A dependency that no API exposes may move to any version in any release. An
  example is `Tmds.DBus.Protocol` (0.x), which Runic.Platform.Linux.DBus uses
  internally.

### Supported platforms

[`eng/support.json`](../support.json) is the source of the current status; the
[README table](../../README.md#supported-platforms) and
`dotnet runic doctor --rid <rid>` are generated from it. From 1.0 a host and RID
pair is supported only when Runic CI builds and runs that host's native layer on
it. "Packaged, not CI-verified" ships and is best effort, outside the promise;
"unsupported" means Runic ships no native assets for it.

CI covers `linux-arm64` and `win-arm64` on hosted Arm runners and the musl RIDs
in an Alpine container, so they are supported where the host ships native
assets. The 1.0 status:

| RID | Runic Desktop | CS-WebUI (preview) | WPF |
| --- | --- | --- | --- |
| `linux-x64` | Supported | Supported | n/a |
| `win-x64` | Supported | Supported | Supported |
| `osx-arm64` | Supported | Supported | n/a |
| `linux-arm64` | Supported | Supported | n/a |
| `win-arm64` | Supported | Unsupported | Supported |
| `linux-musl-x64` | Supported | Unsupported | n/a |
| `linux-musl-arm64` | Supported | Unsupported | n/a |
| `osx-x64` | Unsupported | Unsupported | n/a |

- Intel Macs (`osx-x64`) are not a Runic target since 0.12: CI runs only the
  latest macOS on Apple silicon, and `dotnet runic package macos` makes no
  universal bundle. `dotnet runic doctor --runtime osx-x64` fails `target-rid`.
- CS-WebUI ships no native library for musl or `win-arm64`. Publish for a glibc
  RID or `win-x64` (which runs under emulation on Windows on Arm), or use Runic
  Desktop.
- `Runic.Platform.Administration.Windows` supports Windows x64 only, by policy;
  no arm64 or x86 support is planned. On Windows on Arm, publish it for `win-x64`.
- Packages without native assets, such as `Runic.Navigation` and
  `Runic.Application.Testing`, are supported wherever .NET 10 is.

Minimum operating systems and components, from `eng/support.json`:

| OS | Hosts | Minimum |
| --- | --- | --- |
| Windows | Runic Desktop, CS-WebUI, WPF | Windows 11, or a Windows 10 release that .NET 10 supports; Microsoft Edge WebView2 Evergreen Runtime (CS-WebUI needs it only without a Chromium-family browser) |
| macOS | Runic Desktop, CS-WebUI | macOS 15 |
| Linux (glibc) | Runic Desktop | GTK 3 and WebKitGTK 4.1, or GTK 4.12 and WebKitGTK 6.0 with `Runic.Desktop.Gtk4` |
| Linux (glibc) | CS-WebUI | glibc 2.34; GTK 3 and WebKitGTK 4.1 when no Chromium-family browser is installed |
| Linux (musl) | Runic Desktop | The Alpine releases .NET 10 supports, with GTK 3 and WebKitGTK 4.1 (`Runic.Desktop.Gtk4` is not CI-verified on musl) |

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
- **Generated-code support API:** the types that generated code calls, in the
  `Runic.Application.Views.Generated` namespace and the `.Generated` namespaces of
  the ReactiveUI adapters, all `[EditorBrowsable(Never)]`. A minor or patch
  release keeps them binary compatible for code that an earlier release of the
  same major generated (a model library compiled against 1.0 runs on 1.1), but
  their source shape is not for hand-written code and may change. The same rule
  covers the code that the `RUNICDBUS` source generator of
  Runic.Platform.Linux.DBus emits and calls. The generated output itself (file
  names, private members, formatting) is not a contract; the generated public API
  (the composition class and its `AddRunicViews()`/`AddRunicBridges()`, and the
  generated TypeScript client shape) is. On npm, the `./generated`,
  `./generated/wire` and `./generated/mock` entries of `@runic-artifex/views`
  follow the same rule for generated modules, and its `./bindings` entry is
  versioned with the framework adapters, which depend on the exact same release.
  `@runic-artifex/views` reports a .NET `Runic.Application.Views` of another
  release as a diagnostic (`code: "version"`).
- **Experimental APIs in stable packages:** an unfinished API is marked
  `[Experimental]` with one diagnostic ID per API family, listed with its status
  in [`docs/experimental.md`](../../docs/experimental.md). Every use is a compile
  error until the project suppresses the ID, and the API may change or be removed
  in a minor release. An experimental type does not appear in a stable API's
  signature. Graduation removes the attribute and retires the ID, which is
  never reused. IDs follow the numbering in `docs/experimental.md` (from `100`
  where the prefix already names diagnostics, such as `RUNICDBUS100`).
  Experimental now: `RUNICDESK103` (hosted application windows and the Runic
  host protocol, through 1.0).
  Retired: `RUNICNAV001` (Navigation, graduated in 0.7.0-preview.7),
  `RUNICWPF001` (WPF child web presentation, graduated in 0.9.0),
  `RUNICDESK100` (custom Desktop window hosts, graduated in 0.10.0),
  `RUNICDESK101` (the Desktop presentation transport, partly graduated and the
  rest internal in 0.10.0) and `RUNICASSETS001` (the development-directory asset
  source, graduated in 0.10.0), `RUNICDESK104` (the side panel) and
  `RUNICDESK105` (the close drain), both graduated in 0.22.0. An npm package exports its
  experimental APIs only from its `./experimental` subpath, which may change in
  a minor release; graduation moves an export to the root entry. 0.10.0 removed
  every `./experimental` entry; 0.18 added new ones for hosted regions, 0.19
  `onBridgeDraining` for the close drain (in the root entry since 0.22), and 0.20 `runicComponentMount` in
  `@runic-artifex/angular` for Angular guests in trusted component mode; 0.21
  moved trusted component mode of `@runic-artifex/views` and the `component`
  build target of `@runic-artifex/vite-plugin-runic` there.
- **Preview:** `Runic.Application.Views.CsWebUi` is the only preview package. It
  depends on the prerelease `CsWebUi`, so it ships with each release on a
  prerelease version, `<release major.minor.patch>-preview.N` with a counter of its
  own, and may change in any release. `RunicCsWebUiPackageVersion` in
  `eng/Versions.props` sets that version; the `runic-app` template's
  `--host cswebui` references it. `bun run version:bump <version>` advances it, and
  `--cswebui <cswebui-version>` sets it explicitly (see
  [Release candidates and 1.0.0](#release-candidates-and-100)).

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
