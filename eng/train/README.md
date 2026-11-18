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
| `run@<commit>` | The package artifact of the newest successful `ci.yml` run on `main` at or after that commit (a full SHA) |
| `run:<ci run id>` | The package artifact of that successful `ci.yml` run |
| `run` | The package artifact of the newest successful `ci.yml` run on `main`, whatever commit it built. Only for the nightly N-main row and custom runs: the rows T, P-tr and P-sdk refuse it. |
| `local:<dir>` | Packages packed in a checkout (`bun eng/run.mjs pack` writes `artifacts/packages`, with `nuget/` and `npm/`); for a local run before CI. Its result names no CI run, so no publish workflow accepts it. |

A `run:<ci run id>` is a push, scheduled or manual run on `main`, or a pull request's run from a branch of the same
repository, to check a train candidate before it merges (publish workflows only pass `main` runs).

### Run selection by commit

A train input names a commit, never "the newest run" (H2). "The newest" was whatever `main` last built: in the 0.21
train GitHub's `status=success` listing hid the green run for over an hour, so the gate took an older one (I2), and
under a busy merge queue no push run finishes for hours (I6). `run@<commit>` (`selectRunAtOrAfter()` in
`sources.mjs`) follows the rule of the lessons' C6, **the newest green `main` run at or after commit X**:

1. List the repository's `ci.yml` runs on `main`, unfiltered (newest 50), plus the runs of X itself, and filter
   locally: `completed`/`success`, a push, scheduled or manual run of that repository.
2. Newest first, take the first whose head is X or a descendant of X (GitHub's compare API: `identical` or
   `ahead`) and that has its unexpired package artifact. The train feed also needs the pinned version in it.
3. Without one, fail with an error that names X and every run considered and why it was passed over (`not a
   success`, `behind`/`diverged`, no artifact, another version). The usual fix is the lessons' C6: stop enqueueing,
   dispatch `ci.yml` on `main` once, let it finish, and run the step again.

A descendant is accepted, not only X's own run, because the train needs *a* green run that includes X, and X's own
push run is often the one the merge queue replaced (I6). Whether a newer upstream commit changed the packages is
the movement check's question ("Choreography", step 5), not the selection's.

Publish uses the stricter rule of its own commit: `eng/release/ci-run.mjs` (and Translations'
`eng/release/ci-artifact.mjs find-ci`) take only a successful `main` run **of the commit being published**
(`head_sha`, unfiltered listing), because those are the bytes it publishes.

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

## Train feed

Before the first publish of a train, runic-sdk's `main` builds and tests against the Command Line and Translations
releases of that train, which are not on nuget.org yet (COMPATIBILITY.md 3.4). `Directory.Packages.props` names them
(`Runic.CommandLine`, `Runic.Translations`); the template's pins and `.config/dotnet-tools.json` follow.

- **`eng/ci/train-feed.mjs prepare`** (the `plan` job of `ci.yml`; `setup-sdk` with `train-feed: prepare` in
  `nix.yml` and `dependency-audit.yml`) checks nuget.org for each of the two versions. A version that nuget.org lacks
  names the upstream commit of the train candidate on its `PackageVersion`, after `Version`:
  `<PackageVersion Include="Runic.Translations" Version="0.14.0" TrainCommit="<full SHA>" />` (only the anchor
  package needs it). The feed downloads the package artifact of the newest successful `ci.yml` run on that
  repository's `main` **at or after that commit** with exactly that version ("Run selection by commit";
  `--cli run@<commit>|run:<id>`, `--translations run@<commit>|run:<id>` override it), with `GITHUB_TOKEN` as the
  gate does; without a `TrainCommit` it fails rather than take the newest run. It uploads the packages with
  `train-feed.json` (each run and package SHA-256) as the **`train-feed`** artifact. The artifact's npm packages
  whose version the npm registry lacks (Translations' `@runic-artifex/*-translations*`) go into its `npm/` folder. With both published it does
  nothing, and nothing else changes.
- **Restores** (`setup-sdk` sets `RUNIC_TRAIN_FEED`): `eng/build/nuget-lock.targets` adds the feed as a source and
  restores unlocked against a lock path in `obj/`, because a later candidate of the same version has other bytes, and
  a candidate from a commit other than the published one never matches nuget.org's package (NU1403 after
  publication). The lock `contentHash` covers the package content without its signature, so nuget.org's repository
  signature alone changes nothing: a candidate built from the published commit keeps its hashes (0.22 train,
  Translations 0.14.0); one built from an earlier commit does not (0.21 train, Translations 0.13.0). `setup-sdk` restores the repository's tools once with `--add-source`, caches
  NuGet packages under `nuget-train-*` so candidates never seed a later locked restore, and the consumers outside
  the repository (`tests/templates/Test-Templates.sh`, `eng/verify-packages.mjs`) add the feed to their
  `NuGet.config`. `eng/verify-packages.mjs`, the only npm consumer of a Translations package
  (`@runic-artifex/vite-plugin-runic-translations`), installs its tarball (`file:`) while npm lacks that version
  (`trainFeedNpmSpec()`).
- **Not publishable.** `eng/release/ci-run.mjs` refuses a CI run with a `train-feed` artifact, so Publish preview
  never publishes packages built on candidates.
- **Locally:** `GH_TOKEN=$(gh auth token) bun eng/ci/train-feed.mjs prepare`, then
  `export RUNIC_TRAIN_FEED=$PWD/artifacts/train-feed/nuget` and `dotnet tool restore --add-source "$RUNIC_TRAIN_FEED"`
  once. Candidates enter `.cache/nuget` under their release versions; delete those folders
  (`.cache/nuget/runic.commandline*/<version>`, `runic.translations*/<version>`, `dotnet-runic-translations/<version>`)
  after the publication, and the tool resolver cache entry (`~/.dotnet/toolResolverCache/1/dotnet-runic-translations`)
  that points at them.
- The committed lock files name the train versions and hash the candidates (`bun run lock:nuget` with
  `RUNIC_TRAIN_FEED` set), so their diff shows the new graph. `nix/tests/completions.nix`' fixture
  (`tests/fixtures/nix/completions/App`) stays on the published Command Line, because Nix fetches by lock-file hash
  from nuget.org; `nix/examples.nix` leaves out `Runic.Application.Views.Translations`' lock file for the same reason.

**Once Command Line and Translations are published** (D043.7 steps 2 and 3), before runic-sdk's publish:

1. Delete the candidates from the NuGet cache (above), unset `RUNIC_TRAIN_FEED`, drop the `TrainCommit`
   attributes, and run `bun run lock:nuget`: the
   lock files now hash nuget.org's packages. Only `contentHash` lines may change, and none do when the candidate was
   built from the published commit; the run is still the check. If any changed, CI without the feed fails the locked
   restore with NU1403 until this merges.
2. Move `tests/fixtures/nix/completions/App` to the new Command Line (`App.csproj`, then `dotnet restore` its lock
   file).
3. Merge that; its CI has no `train-feed` artifact. Row **P-sdk** then runs on it as part of Publish preview
   (dry run or not; "Publish workflows" below).

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
| T | `run:<id>` | `run@<commit>` | `run@<gate SHA>` | runic-cli-sdk's publish of the train | yes |
| P-tr | `published:<train>` | `run:<id>` | `run@<gate SHA>` | runic-translations-sdk's publish | yes |
| P-sdk | `published:<train>` | `published:<train>` | `run:<id>` | runic-sdk's publish | yes |
| N-pub | `published` | `published` | `published` | nightly, and pull requests that change the gate | the pull request only |
| N-main | `run` | `run` | `run` | nightly, and pull requests that change the gate | no (informational) |

The `<id>` is the CI run whose packages the publish workflow is about to publish. The other repositories' inputs
name a commit: for runic-sdk the gate's own pinned SHA (`gate-ref`, the commit the train candidate merged as or a
later `main` commit), for Translations in row T its train commit. The rows T, P-tr and P-sdk refuse a bare `run`.

```sh
gh workflow run train-compat.yml -R Runic-Artifex/runic-sdk -f row=T -f cli=run:<id> -f translations=run@<sha> -f sdk=run@<sha>
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
verdict, and `trainCandidate`: the movement check ("Choreography", step 5) with the runic-sdk run, whether it had
a train feed, and what it checked or refused. The exit code is 0 when no required scenario has an unexpected failure, 1 when one has, and 3 when a CI
artifact was refused.

## Expected failures

`expected-failures.json` names the incompatibilities of published packages that the train fixes, so that the gate
can pass against today's packages while still showing them. Each entry has an id from COMPATIBILITY.md, the failures
it explains (scenario, optional check, a regular expression) and `applies`: it only counts while the named package
is below the named version in the combination. An entry therefore cannot excuse a failure of the train's own
candidates, and an entry that applied but matched nothing is reported as "no longer reproduces; remove the entry".
An entry may `waive` a warning code (I1's NU1608): when every error of a build is such an expected failure, the
scenario records it and continues with the warning waived, so the rest of the scenario still checks something.

The file has no entries now. The 0.18 train had five, all in Translations 0.9.x packages and fixed by Translations
0.10.0, and they went once that was published: I1 (`Runic.Translations.CommandLine` required `Runic.CommandLine`
`[0.9.1, 0.10.0)`: NU1608), I2 (no German `hints.usage`), I3 (the editor pinned CsWebUi `2.5.0-beta.4.8`), I4
(Hosting and Wpf ranges, the adapter's Command Line bound) and I7 (`dotnet-runic-translations --version` printed
`0.2`). A later train adds the incompatibilities it fixes in the same form.

## Cross-repository artifacts

The workflow reads the other repositories' artifacts with the job's `GITHUB_TOKEN` (D061.12). If GitHub refuses
that, the gate stops with exit code 3 and an error naming the repository; the maintainer then provides a read-only
token or GitHub App with `actions: read` on the three repositories as the secret `RUNIC_TRAIN_TOKEN`, which the
workflow already prefers when it exists (`train-result.json` records which one was used).

## Publish workflows

A publish workflow calls the gate as a reusable workflow and makes its publishing job need it; a dry run runs it
too. `eng/release/README.md` describes runic-sdk's.

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
      sdk: run@<the same SHA>
      gate-ref: <the same SHA>
  publish:
    needs: [preflight, candidate, train]
```

Follow-up per repository:

- **runic-cli-sdk** (`publish-preview.yml`): a `train` job as above with `row: T`,
  `cli: run:<the CI run id the preflight job found>`, `translations: run@<Translations commit of the train>`,
  `sdk: run@<the gate SHA>`; the publishing job needs it. Pin the reusable workflow to a runic-sdk SHA. The dry run
  runs it too. Its pin (6584dc375) predates `run@`; moving the pin past this change needs the two inputs changed.
- **runic-translations-sdk** (`publish-preview.yml`): the same with `row: P-tr`,
  `cli: published:<RunicCommandLineVersion from eng/Versions.props>`, `translations: run:<its CI run id>`,
  `sdk: run@<the gate SHA>`.
- **runic-sdk** (`publish-preview.yml`): a `train` job with `row: P-sdk`,
  `cli: published:<Runic.CommandLine>`, `translations: published:<Runic.Translations>` (the versions in
  `Directory.Packages.props`, which the preflight job reads with `trainVersions()` from `eng/ci/train-feed.mjs`) and
  `sdk: run:<the CI run id preflight found>`. It calls `./.github/workflows/train-compat.yml`, so the gate runs from
  the commit being published, without `gate-ref`.

## Choreography

### Most changes need no train

A release train is needed only when runic-sdk cannot build against the published upstream release and the new one
at once. D069.3 (from 0.22) makes cross-repository breaking changes ship with a one-minor transition: the upstream
release adds the new name and keeps the old one `[Obsolete]` for one minor. Then:

1. The upstream repository (Translations, say 0.14.0) publishes on its own; its P-tr gate runs against the published
   runic-sdk (`sdk: published`) or runic-sdk `main` (`run@<gate SHA>`), and nothing in runic-sdk waits for it.
2. runic-sdk adopts the published release in an ordinary pull request (`Directory.Packages.props`, lock files,
   template pins), moving off the obsolete names. No train feed, no freeze.
3. The upstream removes the old names in the next minor, which runic-sdk already no longer uses.

Only an unavoidable break (a change that cannot keep the old shape for a minor) runs the train below. 0.20 and 0.21
were trains because Translations renamed `Add` → `AddCatalog` and removed `RUNIC_TRANSLATIONS_EDIT_ALLOW` without a
transition (I5).

### A Translations-then-SDK train

The loop to break (I4): runic-sdk builds against the Translations candidate through the train feed; Translations'
publish needs a green runic-sdk run that used it; and every Translations commit packs a different candidate. The
steps are in order; the coordinator owns them.

1. **Freeze Translations `main`** when its release pull request (version, notes) is ready to merge. From that merge
   until the publish, nothing else merges into Translations `main`: no dependency bumps, no workflow or docs
   changes. While archives are not reproducible (H1), every commit changes the candidate's bytes; afterwards a
   commit that leaves the packages alone passes step 5, but the freeze stays the rule.
2. **Pin the gate in the release pull request, not after it.** The release pull request also moves
   `publish-preview.yml`'s `train-compat.yml@<SHA>`, its `gate-ref` and `sdk: run@<SHA>` to one runic-sdk commit
   that has the gate the release needs (normally runic-sdk `main`). A separate pin pull request after the release
   moves Translations `main` and with it the candidate (0.20, 0.21 needed two). If runic-sdk's train candidate has
   not merged yet, pin to the commit it will descend from (runic-sdk `main` now): `run@` takes the newest green
   `main` run at or after it, which will be the train candidate's once it merged and its push run is green.
3. **runic-sdk's train candidate** (one pull request): `Directory.Packages.props` names the Translations version
   with `TrainCommit=` the frozen Translations `main` commit from step 1; the adaptations to the break; the lock
   files written with the feed. Its CI uses the train feed and uploads the `train-feed` artifact. Merge it.
4. **A green runic-sdk `main` run that includes it.** Stop enqueueing runic-sdk pull requests, dispatch `ci.yml` on
   `main` once (`gh workflow run ci.yml -R Runic-Artifex/runic-sdk --ref main`) unless a push run of the train
   candidate's merge is already running, and let it finish (C6; CLAUDE.md "CI hygiene").
5. **Translations dry run, then publish** (`publish-preview.yml` on the frozen commit; the dry run first). Its P-tr
   gate resolves `sdk: run@<gate SHA>` to that green run, and before building anything **checks that Translations
   `main` did not move**: the runic-sdk run's `train-feed.json` names the Translations run and commit its feed
   served; the gate's Translations input (the CI run of the commit being published) must be that commit, or, when
   `main` moved, every package the feed served must be byte-identical in the candidate (same SHA-256; with H1's
   reproducible archives a commit that changed no package input passes). Otherwise the gate fails at once with
   "main moved since runic-sdk's train candidate used it" and names both runs: publish the commit the candidate
   used, or move `TrainCommit` and rebuild runic-sdk's candidate. A runic-sdk run without a train feed (no train)
   skips the check.
6. **Feed exit** right after the publish, as soon as nuget.org and npm serve the packages ("Train feed", "Once
   Command Line and Translations are published"): drop `TrainCommit`, delete the cached candidates, rewrite the lock
   files against nuget.org. Until it merges every runic-sdk pull request still builds on the feed.
7. **Unfreeze Translations `main`** after its publish. Its gate pin stays valid; move it to runic-sdk `main` with
   the next Translations change that needs a newer gate.
8. runic-sdk publishes from the feed exit's commit or later (row P-sdk, `published:` Translations).

A Command Line train adds the same steps before Translations (row T: `translations: run@<Translations commit>`,
`sdk: run@<gate SHA>`), and the movement check covers Command Line's input the same way.
