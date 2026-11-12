# Train compatibility gate

Runic Command Line (runic-cli-sdk), Runic Translations (runic-translations-sdk) and Runic SDK release as one train
(D060, D043.7: Command Line, then Translations, then SDK). This gate restores, builds and runs real consumers of one
combination of the three repositories' packages with warnings as errors, before any of them is published (D061.11 to
D061.20). The audit that designed it and the incompatibilities it names (I1 to I7) are in
`local-planning/design/w319-1.0-scope/train-0.18/COMPATIBILITY.md`.

- `.github/workflows/train-compat.yml` runs it on GitHub (Linux x64 only).
- `eng/train/gate.mjs` is the gate. The other modules are `sources.mjs` (inputs, nuget.org, CI artifacts),
  `nuget.mjs` (repositories, nuspecs, the isolated `nuget.config`), `scenarios.mjs` (the consumers), `checks.mjs`
  (key coverage, ranges and pins), `expected.mjs` with `expected-failures.json` (the allow-list) and `versions.mjs`.
- `train.test.mjs` runs in the engineering job of `ci.yml`. A change below `eng/train/` plans no product jobs
  (`engineeringOnly` in `eng/run.mjs`), and a pull request that changes the gate runs `train-compat.yml` itself.

## Inputs

Each repository gets one input:

| Input | Packages |
| --- | --- |
| `published` | The newest version on nuget.org (of `Runic.CommandLine`, `Runic.Translations`, `Runic.Application.Views`) |
| `published:<version>` | That version on nuget.org. A package on its own prerelease line joins with its newest prerelease of that release (`Runic.Application.Views.CsWebUi` 0.17.1-preview.1 for 0.17.1). |
| `run` | The package artifact of the newest successful `ci.yml` run on `main` |
| `run:<ci run id>` | The package artifact of that successful `ci.yml` run |

The artifacts are `command-line-packages` (runic-cli-sdk), `runic-translations-packages` (runic-translations-sdk)
and `runic-sdk-<run id>` (runic-sdk). The gate downloads them through the REST API with `GH_TOKEN`, records each
file's SHA-256 and gives each repository with a `run:` input its own local feed. The package set of a published
version is every package of the `runic-artifex` nuget.org account that has that release.

Candidates carry final release versions, so they must never enter a shared cache. The gate works in a fresh
directory outside the repository (`$RUNNER_TEMP`, or the system temporary directory) with its own `NUGET_PACKAGES`,
NuGet HTTP cache, `DOTNET_CLI_HOME` (so also its own tool resolver cache) and npm cache. Its `nuget.config` maps the
packages to sources: `Runic.CommandLine*` to the Command Line feed, `Runic.Translations*` and
`dotnet-runic-translations` to the Translations feed, every other `Runic.*` and `dotnet-runic` to the SDK feed, and
everything else to nuget.org. A repository whose input is `published` maps to nuget.org. A candidate can therefore
never be replaced by a nuget.org package with the same id and version. runic-sdk's npm candidates are served by
`tests/templates/template-npm-registry.mjs`, and the template's `package-lock.json` is bound to their integrities as
in runic-sdk's template tests. Translations' npm packages have no `@runic-artifex` peers and no consumer here.

## Scenarios

All consumers build with `TreatWarningsAsErrors` and NuGet audit on, outside the repository (no runic-sdk
`Directory.Build.props`, central package management or `global.json` applies).

| Id | Consumer | Checks |
| --- | --- | --- |
| S1 | A Command Line app in German through `Runic.Translations.CommandLine`, NativeAOT (`IlcTreatWarningsAsErrors`) | publish; `greet Ada` prints `Hallo Ada`, exit 0; `greet --unknown` exits 2 with the German diagnostic; `greet --help` exits 0; no English framework line in German output. The `--format json` envelope and per-outcome exit codes join once Command Line 0.12.0 defines them (runic-cli-sdk#88, #89). |
| S2 | Key coverage | Every key of Command Line's key list has a German message in the adapter's packed `runic-command-line/de.rmf2`, no removed key has one, and every message names a key. The key list is `text-keys.json` in the `Runic.CommandLine` package (D061.19, below). |
| S3 | `dotnet new runic-app --translations true --tests true`, plus `Runic.Translations.Hosting`, `Runic.Application.Views.Translations` and a `cli` front end through Translations.CommandLine (the README patterns) | `dotnet tool restore`, `dotnet build`, `dotnet test`; `cli hello` without its argument exits 2 in German |
| S4 | `dotnet new runic-app --host cswebui --frontend svelte --translations true --tests true` | `dotnet tool restore`, build, test |
| S5 | The template's `.config/dotnet-tools.json` | It pins this train's `dotnet-runic` and `dotnet-runic-translations` (R5); both restore and report their version; `dotnet runic doctor` passes after a restore, apart from its checks of the machine (`desktop-runtime`, `platform-services`, `browser`, `javascript-runtime`, `package-manager`) |
| S6 | Every candidate nuspec, the template package and the tools | One release per repository; every cross-repository dependency, and Translations.CommandLine, Hosting and Wpf on `Runic.Translations` (D061.16), is `[F, x.(y+1).0-0.0)` with F on this train's line (lockstep minors), the upper bound in the `-0.0` form (D061.13) and never `-0`; the template's `Runic.Translations`, `.Build` and tool pins equal the Translations version; `dotnet-runic`, `Runic.Create`, `Runic.Assets` and `dotnet-runic-translations` bundle this train's `Runic.CommandLine` (D061.15). `Runic.Application.Views.Translations` → `Runic.Translations.Hosting` stays open by design (Q6). |
| S7 | WPF library with `Runic.Application.Views.Wpf`, `.Views.Translations`, `Runic.Translations.Wpf`, `.Hosting`, `Runic.CommandLine` (`EnableWindowsTargeting`) | restore and build on Linux |
| S8 | The Translations editor at the Translations input's commit, built with `RunicSdkDependencyVersion` set to the SDK input | restore and build without its frontend; **informational**, never fails the gate |

A template change that a scenario's edits no longer fit fails with "The template's … changed"; update
`scenarios.mjs` with it.

### Command Line's key list

S2 reads the first `text-keys.json` in the `Runic.CommandLine` package. It accepts an array or `{ "keys": [...] }`;
each item is a key string or an object with `key` (or `id`, `name`), optionally `removed: true` or `removedIn`
(the adapter must not have a message for it) and `message: false` or `null` (a key without a catalog message, like
`faults.RCLI4001`). Command Line 0.11.0 and earlier ship no `text-keys.json`: for them S2 reads the key table of
`docs/guides/command-line/text-keys.md` at the release tag (or a run's commit) and says so in its notes. From
Command Line 0.12.0 a package without `text-keys.json` fails S2.

## Rows

| Row | Command Line | Translations | SDK | When | Blocks |
| --- | --- | --- | --- | --- | --- |
| T | `run:<id>` | `run` | `run` | runic-cli-sdk's publish of the train | yes |
| P-tr | `published:<train>` | `run:<id>` | `run` | runic-translations-sdk's publish | yes |
| P-sdk | `published:<train>` | `published:<train>` | `run:<id>` | runic-sdk's publish | yes |
| N-pub | `published` | `published` | `published` | nightly, and pull requests that change the gate | the pull request only |
| N-main | `run` | `run` | `run` | nightly, and pull requests that change the gate | no (informational) |

The `<id>` is the CI run whose packages the publish workflow is about to publish. Before the first publish of a
train all three release changes are on `main`, so `run` (the newest successful `main` run) is the train's candidate
of the other repositories.

```sh
gh workflow run train-compat.yml -R Runic-Artifex/runic-sdk -f row=T -f cli=run:<id> -f translations=run -f sdk=run
# locally, from the dev shell (GH_TOKEN only for run inputs):
GH_TOKEN=$(gh auth token) bun eng/train/gate.mjs --cli published --translations published --sdk run --out artifacts/train-compat
bun eng/train/gate.mjs --only S2,S6          # the package checks only, in seconds
```

Locally, `RUNIC_TRAIN_NUGET_PACKAGES=~/.nuget/packages` reuses a NuGet cache when every input is `published`; with
any `run:` input the gate always uses its own. `--keep` keeps the work directory.

## Result

The gate writes `train-result.json`, `summary.md` (also the job summary) and one log per command to `--out`; the
workflow uploads them as `train-result-<run id>-<row>`. `train-result.json` records the row, the gate's commit and
run, every input with its resolved version, CI run (id, URL, commit, artifact id) and each package's id, version
and SHA-256, the status of each scenario with its failures and notes, the expected failures that applied and the
verdict. The exit code is 0 when no required scenario has an unexpected failure, 1 when one has, and 3 when a CI
artifact was refused.

## Expected failures

`expected-failures.json` names the incompatibilities of published packages that the train fixes, so that the gate
can pass against today's packages while still showing them. Each entry has an id from COMPATIBILITY.md, the failures
it explains (scenario, optional check, a regular expression) and `applies`: it only counts while the named package
is below the named version in the combination. An entry therefore cannot excuse a failure of the train's own
candidates, and an entry that applied but matched nothing is reported as "no longer reproduces; remove the entry".
An entry may `waive` a warning code (I1's NU1608): when every error of a build is such an expected failure, the
scenario records it and continues with the warning waived, so the rest of the scenario still checks something.

| Id | What | Applies while | Remove |
| --- | --- | --- | --- |
| I1 | `Runic.Translations.CommandLine` 0.9.x requires `Runic.CommandLine` `[0.9.1, 0.10.0)`: NU1608 with Command Line 0.11.0 (S1, S3); its range and the tool's bundled Command Line 0.9.1 (S6) | Translations.CommandLine < 0.10.0 | runic-sdk 0.18.0 release PR, after Translations 0.10.0 is published |
| I2 | No German `hints.usage` in the adapter: one English line (S1, S2, S3) | Translations.CommandLine < 0.10.0 | the same |
| I4 | Hosting and Wpf accept any `Runic.Translations` ≥ 0.9.1; the adapter's Command Line bound is `0.10.0`, not `-0.0` (S6) | Runic.Translations < 0.10.0 | the same |
| I7 | `dotnet-runic-translations --version` prints `0.2`, a constant in `tools/dotnet-runic-translations/Program.cs` that SARIF reports carry too (S5) | dotnet-runic-translations < 0.10.0 | the same |
| I3 | The editor pins CsWebUi `2.5.0-beta.4.8`; Views.CsWebUi 0.17.1-preview.1 needs beta.4.9 (NU1109, S8, informational) | Runic.Translations < 0.10.0 | the same |

With the newest published versions of 2026-11-12 (Command Line 0.11.0, Translations 0.9.1, SDK 0.17.1) every
scenario's failures are these, S4 and S7 pass, and the verdict is pass.

## Cross-repository artifacts

The workflow reads the other repositories' artifacts with the job's `GITHUB_TOKEN` (D061.12). If GitHub refuses
that, the gate stops with exit code 3 and an error naming the repository; the maintainer then provides a read-only
token or GitHub App with `actions: read` on the three repositories as the secret `RUNIC_TRAIN_TOKEN`, which the
workflow already prefers when it exists (`train-result.json` records which one was used).

## Publish workflows

A publish workflow calls the gate as a reusable workflow and makes its publishing job need it. In runic-sdk this
comes with the 0.18.0 release PR (COMPATIBILITY.md Q10); `eng/release/README.md` describes it.

```yaml
  train:
    needs: preflight
    permissions:
      contents: read
      actions: read
    uses: Runic-Artifex/runic-sdk/.github/workflows/train-compat.yml@<runic-sdk commit SHA> # main
    with:
      row: P-tr
      cli: published:0.12.0
      translations: run:${{ needs.preflight.outputs.ci-run-id }}
      sdk: run
      gate-ref: <the same SHA>
  publish:
    needs: [preflight, candidate, train]
```

Follow-up per repository:

- **runic-cli-sdk** (`publish-preview.yml`): a `train` job as above with `row: T`,
  `cli: run:<the CI run id the preflight job found>`, `translations: run`, `sdk: run`; the publishing job needs it.
  Pin the reusable workflow to a runic-sdk SHA. The dry run runs it too.
- **runic-translations-sdk** (`publish-preview.yml`): the same with `row: P-tr`,
  `cli: published:<RunicCommandLineVersion from eng/Versions.props>`, `translations: run:<its CI run id>`,
  `sdk: run`. Its 0.10.0 also fixes I1, I2, I4, I7 (the tool's version) and I3, after which runic-sdk removes
  those entries.
- **runic-sdk** (`publish-preview.yml`, in the 0.18.0 release PR): `row: P-sdk`, `cli: published:<train>`,
  `translations: published:<train>`, `sdk: run:<its CI run id>`; then remove the I-entries of
  `expected-failures.json`.
