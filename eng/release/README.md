# Releasing Runic SDK

This is the SDK's current release policy. It replaces the first-preview acceptance
plans and the SDK portions of the organization launch/release runbooks. Historical
releases and their evidence remain historical records.

## Routine preview release

1. Commit the intended preview version and user-facing changes. Package versions
   must agree with `eng/workspace.json`; published versions cannot be overwritten.
2. Run **Publish preview** (`publish-preview.yml`) on `main`, entering that version.
3. The workflow runs the existing full CI, publishes its exact package artifacts,
   then creates the GitHub prerelease with generated change notes, a package bundle and one checksum file.

Full CI includes package/template consumers and native JIT/NativeAOT checks. It is
reused directly by the release workflow. There is no separate acceptance workflow,
receipt upload, candidate approval dossier, mandatory soak, or independent pilot
prerequisite. Manual checks are appropriate when a change affects native/UI
behavior that automation cannot cover. Soaks and matched benchmarks remain useful
for relevant changes and regression investigations; they are not routine gates.

Keep release notes focused on changes, installation, migration and meaningful
known issues. Put logs and internal package metadata in Actions artifacts. Record
nonblocking limitations as issues rather than introducing custom waiver formats.

## Publishing setup and retries

Keep the existing `preview` environment and trusted publishers for
`Runic-Artifex/runic-sdk`, workflow `publish-preview.yml`, environment `preview`.
NuGet uses `NuGet/login` and `vars.NUGET_USER` (the profile username); npm publishes
through OIDC with the `preview` tag. This workflow does not move npm `latest`.
The filename is retained so installed trusted-publisher registrations keep working.
New package identities may still need registry ownership/bootstrap configuration.
That is account setup, not a recurring release acceptance checklist.

If publication fails, rerun the failed job in the same run. If NuGet is still
indexing a previous push, wait for it to become available before retrying.
It reuses the tested artifacts, checks already published versions for matching
contents, and publishes only missing packages. Do not rerun successful producers
unnecessarily. Changed package contents require a new version. Registry indexing and public installation do not block GitHub release creation;
NuGet can take up to an hour to expose newly accepted packages. Assets upload to a draft before
it becomes public, so interrupted uploads can be resumed. An existing published
release for the same source is preserved on retry. If artifacts expire, prepare a new version/run.

After indexing, optional diagnostics can be run with `bun eng/release/cli.mjs
registry <manifest>` and `bun eng/release/smoke.mjs`. Download the manifest from
the run’s release diagnostics and use the released source checkout. These checks
do not republish packages.

The smoke installs a .NET library and the CLI tool, runs them, and installs/imports
the npm application bridge outside the checkout. The wider template/framework
matrix already runs before publication. A smoke failure is actionable; it does not
trigger an additional manual matrix automatically.

For packaging/release-tool changes, use focused checks:

```sh
bun run test eng/release/contracts.test.mjs
bun run test eng/release/workflow.test.mjs
bun test eng/release/template-locks.test.mjs
```

`cli.mjs prepare` records the package inventory and hashes for the current run;
`verify` checks the downloaded files; `publish` checks registry identity/content
before sending missing versions; `registry` checks availability. These are internal
workflow steps, not files a maintainer must assemble by hand.

After publication, refresh the public docs with `bun run docs:release <version>`.
This records the released tag's package catalog in one file. Commit it with any
API guide changes and deploy the docs; see [docs maintenance](../../docs/README.md).
The marketing site links to the docs and needs no routine version edits.
