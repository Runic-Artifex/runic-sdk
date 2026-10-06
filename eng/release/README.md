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
6. Afterwards, move each library's `PublicAPI.Unshipped.txt` entries into
   `PublicAPI.Shipped.txt`. A `*REMOVED*` entry is not moved: delete it together
   with the Shipped line it names. Then set `RunicPackageValidationBaselineVersion`
   in `eng/Versions.props` to the published version and delete the
   `CompatibilitySuppressions.xml` files, which describe breaks from the old baseline.
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
  (which versions are missing; published versions must have matching contents),
  `github.mjs release --dry-run` (the tag and any release belong to this commit) and
  `cli.mjs tag-latest --dry-run` (where npm `latest` would move).
- `publish` runs only when **dry-run** is unchecked. It is the only job in the
  `preview` environment and the only one with `id-token: write`,
  `attestations: write` and `contents: write`. It downloads the same artifact and
  the candidate files, checks the files against `release-sha256`, verifies the
  packages against the candidate inventory, and attests those verified bytes before
  anything is published: `actions/attest-build-provenance` covers every `.nupkg`,
  every npm `.tgz` and the three release assets; `actions/attest` attaches the SBOM
  to the packages and the bundle. Then it publishes (npm also with `--provenance`),
  creates the release and moves `latest`. It installs no workspace dependencies:
  the release scripts use only built-in modules.

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
New package identities may still need registry ownership/bootstrap configuration.
That is account setup, not a recurring release acceptance checklist.

If publication fails, rerun the failed `publish` job in the same run. If NuGet is still
indexing a previous push, wait for it to become available before retrying.
It reuses the same CI artifact, checks already published versions for matching
contents, and publishes only missing packages. Changed package contents require a new version. Registry indexing and public installation do not block GitHub release creation;
NuGet can take up to an hour to expose newly accepted packages. Assets upload to a draft before
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
bun test eng/release/github.test.mjs eng/release/sbom.test.mjs
bun run test eng/release/ci-run.test.mjs
bun test eng/release/template-locks.test.mjs
```

`cli.mjs prepare` records the package inventory and hashes for the current run;
`verify` checks the downloaded files; `publish` checks registry identity/content
before sending missing versions; `registry` checks availability. These are internal
workflow steps, not files a maintainer must assemble by hand.

## After publication: docs catalog

The release workflow does not push to other repositories. After publication,
refresh the public portal from the `runic-site` repository with
`bun run docs:release <version>` (the publish run's summary repeats this).
This records the released tag's package catalog in one file. Commit it with any
API guide changes and deploy the docs; see [portal maintenance](https://github.com/Runic-Artifex/runic-site/blob/main/docs/README.md).
The marketing site links to the docs and needs no routine version edits.
