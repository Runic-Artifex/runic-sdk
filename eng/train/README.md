# Train compatibility gate

Runic Command Line (runic-cli-sdk), Runic Translations (runic-translations-sdk) and Runic SDK release as one train
(D060, D043.7: Command Line, then Translations, then SDK). This gate restores, builds and runs real consumers of one
combination of the three repositories' packages with warnings as errors, before any of them is published (D061.11 to
D061.20). The audit that designed it and the incompatibilities it names (I1 to I7) are in
`local-planning/design/w319-1.0-scope/train-0.18/COMPATIBILITY.md`.

- `.github/workflows/train-compat.yml` runs it on GitHub (Linux x64 only).
- `eng/train/gate.mjs` is the gate. The other modules are `sources.mjs` (inputs, nuget.org, CI artifacts),
  `nuget.mjs` (repositories, nuspecs, the isolated `nuget.config`), `scenarios.mjs` (the consumers), `checks.mjs`
  (key coverage, ranges and pins), `expected.mjs` with `expected-failures.json` (the allow-list), `patch.mjs` (the
  patch path, "Patch releases") and `versions.mjs`.
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

1. List the repository's `ci.yml` runs on `main`, unfiltered (newest 50), plus the runs of X itself, and its merge
   queue runs (newest 50, plus those of X), and filter locally: `completed`/`success`, a push, scheduled, manual or
   merge queue run of that repository.
2. Newest first, take the first whose head is X or a descendant of X (GitHub's compare API: `identical` or
   `ahead`) and that has its unexpired package artifact. A merge queue run counts only when its commit is on `main`
   (`main` is that commit or a descendant): the queue moves `main` to exactly the commit its run tested, so a passed
   queue run of a commit on `main` is that commit's `main` run (D077.22), while an entry whose group failed never
   reaches `main`. A push run that its queue run covered runs nothing and uploads no artifact
   ([eng/ci/README.md](../ci/README.md), "Merge queue"), so its queue run is the one taken. The train feed also
   needs the pinned version in it.
3. Without one, fail with an error that names X and every run considered and why it was passed over (`not a
   success`, `behind`/`diverged`, no artifact, a queue run of a commit not on `main`, another version). Usually X
   has not merged yet, or its queue run failed; once it merges its queue run is there. Otherwise dispatch `ci.yml`
   on `main` once, let it finish, and run the step again.

`run` (the N-main row) takes the newest such run with an artifact, a queue run of a commit on `main` included, and
`run:<id>` also accepts a merge queue run, which is what runic-sdk's publish passes when the release's queue run is
the run it found (`eng/release/ci-run.mjs`).

A descendant is accepted, not only X's own run, because the train needs *a* green run that includes X, and before
the queue run counted, X's own push run was often the one the merge queue replaced (I6). Whether a newer upstream commit changed the packages is
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

**`eng/ci/train-feed.mjs exit`** computes steps 1 and 2. Every `PackageVersion` with a `TrainCommit` leaves the
feed, all at once:

- It reads `train-feed.json` of the newest successful runic-sdk `main` run at or after the commit that last changed
  `Directory.Packages.props` whose feed serves each of those versions ("Run selection by commit").
- It waits up to `--wait` minutes (default 70) until nuget.org's flat container lists every NuGet package that feed
  served and npm has every npm package (the rule of runic-sdk's own `indexed` job). A release that is still missing
  fails it with the missing packages: the exit never leaves part of the feed.
- It fails when npm serves other bytes (SHA-256) than the feed's candidate: the template locks then need rebinding,
  which is agent work.
- It drops the `TrainCommit` attributes and moves the Nix fixture to the train's Command Line when it names another
  version. It writes `exit.json` (the versions, TrainCommits, the feed run and the upstream runs it served, each
  release tag's commit, the npm check, the edits) and `pr-body.md` to `--out` (default `artifacts/feed-exit`).
- Without a `TrainCommit` it prints `nothing to exit`. `--dry-run` leaves the checkout alone and prints the edits.
  `--tree <checkout>` computes the exit in another checkout, such as a past `main` commit for a rehearsal.

Then `bun run lock:nuget` (without `RUNIC_TRAIN_FEED`, from a cache filled only from nuget.org), `dotnet restore` of
the Nix fixture when it moved, and **`exit-report`**. It fails unless `Directory.Packages.props` only lost
`TrainCommit="<commit>"` attributes and the fixture only its Command Line version. It adds the changed lock files and
`contentHash` lines to both files, and names lock files where other lines changed.

```sh
GH_TOKEN=$(gh auth token) bun eng/ci/train-feed.mjs exit --wait 1 --dry-run   # read-only: what the exit would change
GH_TOKEN=$(gh auth token) bun eng/ci/train-feed.mjs exit && bun run lock:nuget && bun eng/ci/train-feed.mjs exit-report
```

Prose that names the candidate (docs, upgrading notes) stays agent work.

### Train feed exit workflow

`.github/workflows/train-feed-exit.yml` (`workflow_dispatch` on `main` only) runs these steps and opens the pull
request. The coordinator dispatches it when they approve the last upstream publish of the train; it waits for the
indexing itself:

```sh
gh workflow run train-feed-exit.yml -R Runic-Artifex/runic-sdk --ref main -f dry-run=false
```

| Input | Default | |
| --- | --- | --- |
| `dry-run` | `true` | Compute and print everything; push nothing, open nothing |
| `wait-minutes` | `70` | `exit --wait` (1 to 90) |
| `from` | empty | Dry runs only: a past `main` commit (full SHA) to compute the exit in, as a rehearsal |

- **`exit`** (`contents: read`, `actions: read`, no environment, no secret) checks out `main` (with `from`, also that
  commit as a worktree, with its own .NET SDK). It runs `setup-sdk` with `nuget: none` and no train feed, then
  `train-feed.mjs exit`, `bun run lock:nuget`, the Nix fixture's restore and `exit-report`. Every changed path must
  match `FEED_EXIT_PATHS` (`Directory.Packages.props`, `packages.lock.json` and `packages.<rid>.lock.json` files, the
  Nix fixture's `App.csproj`), nothing below `.github/` and no mode change. It uploads the patch, `base-sha`,
  `exit.json` and `pr-body.md` as `feed-exit-<run id>` (7 days), and the job summary shows the diff. Last, it waits
  up to 60 minutes until the pull request's macOS jobs (`eng/ci/plan.mjs --files`) fit under the cap of 5
  (`eng/ci/runner-wait.py --cap-only`; D077.26(1)). A dry run only reports the cap.
- **`open`** (environment `train`, which admits `main` only; skipped while the repository variable
  `TRAIN_HANDOFF` is `off`) mints the train App's token for runic-sdk, read-only on a dry run, and runs only `git`,
  `gh` and `jq`. It checks out `base-sha` and fails if `main` moved since (real runs). It applies the patch and checks
  the same allowlist again. Then it commits as `runic-artifex-train[bot]` and pushes `train-bot/feed-exit` with
  `--force-with-lease`, the token passed through a one-shot credential helper. Last, it opens the pull request, or
  updates the open one. A branch whose tree already equals the change only gets its body refreshed. A closed,
  unmerged pull request with the same marker (`<!-- train-handoff: feed-exit … -->`) stops it: reopen that one or
  delete the branch.
- The bot never queues, approves or merges. The coordinator merges through the queue after every check; the queue
  run of a `TrainCommit` change runs every leg and is the green `main` run (D077.22).

**Dry runs** (none needs the maintainer; they write nothing):

1. `gh workflow run train-feed-exit.yml -R Runic-Artifex/runic-sdk --ref main` on a `main` without a
   `TrainCommit`: `nothing to exit`, and `open` is skipped. This proves the trigger.
2. `… -f from=a6d6c9f00dce859631886fa20ee7ee838c96b66d` (0.23 step 6): `exit` computes #1244's change without its
   prose (the `TrainCommit` of Translations 0.15.0 and the Nix fixture to Command Line 0.14.0). `open` mints the
   read-only token, applies the patch to that commit, and prints the would-be push, the pull requests and the body.
3. `… -f from=291a8be3b208d04e86fecec9543df99be43aaeaf` (the 0.15.1 candidate): #1259's one-line change.

The rehearsals read the `train-feed` artifacts of those runs, which expire on 2026-12-20.

## Scenarios

All consumers build with `TreatWarningsAsErrors` and NuGet audit on, outside the repository (no runic-sdk
`Directory.Build.props`, central package management or `global.json` applies).

| Id | Consumer | Checks |
| --- | --- | --- |
| S1 | A Command Line app in German through `Runic.Translations.CommandLine`, NativeAOT (`IlcTreatWarningsAsErrors`) | publish; `greet Ada` prints `Hallo Ada`, exit 0; `greet --unknown` exits 2 with the German diagnostic; `greet --help` exits 0; no English framework line in German output. The `--format json` envelope and per-outcome exit codes join once Command Line 0.12.0 defines them (runic-cli-sdk#88, #89). |
| S2 | Key coverage | Every key of Command Line's key list has a German message in the adapter's packed `runic-command-line/de.rmf2`, no removed key has one, and every message names a key. The key list is `text-keys.json` in the `Runic.CommandLine` package (D061.19, below). |
| S3 | `dotnet new runic-app --translations true --tests true`, plus `Runic.Translations.Hosting`, `Runic.Application.Views.Translations` and a `cli` front end through Translations.CommandLine (the README patterns) | `dotnet tool restore`, `dotnet build`, `dotnet test`; `cli hello` without its argument exits 2 in German |
| S4 | `dotnet new runic-app --host cswebui --frontend svelte --translations true --tests true` | `dotnet tool restore`, build, test |
| S5 | The template's `.config/dotnet-tools.json` | It pins this train's `dotnet-runic` and `dotnet-runic-translations` (R5; for a Translations patch, after the patch path bound it); both restore and report their version; `dotnet runic doctor` passes after a restore, apart from its checks of the machine (`desktop-runtime`, `platform-services`, `browser`, `javascript-runtime`, `package-manager`) |
| S6 | Every candidate nuspec, the template package and the tools | One release per repository; every cross-repository dependency, and Translations.CommandLine, Hosting and Wpf on `Runic.Translations` (D061.16), is `[F, x.(y+1).0-0.0)` with F on this train's line (lockstep minors), the upper bound in the `-0.0` form (D061.13) and never `-0`; the template's `Runic.Translations`, `.Build` and tool pins equal the Translations version, or are an earlier patch of its line that the patch path binds; `dotnet-runic`, `Runic.Create`, `Runic.Assets` and `dotnet-runic-translations` bundle this train's `Runic.CommandLine` (D061.15), or for a Command Line patch an earlier patch of its line. `Runic.Application.Views.Translations` → `Runic.Translations.Hosting` stays open by design (Q6). |
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

### When a train is needed

The version rules (COMPATIBILITY.md 3.6) decide it, not the size of the change:

- **R1 lockstep.** Each Translations minor supports exactly one Command Line minor, and runic-sdk's
  `Runic.Application.Views.Translations` follows each Translations minor (its range is `[x.y.0, x.(y+1).0-0.0)`).
- **R5 literal pins.** runic-sdk's template pins `Runic.Translations`, `.Build`, the `dotnet-runic-translations`
  tool and the Translations Vite plugin's npm version literally, and the gate checks them against the release under
  test (S5, S6).

So:

| Upstream release | runic-sdk must pin it before it publishes? | Path |
| --- | --- | --- |
| A **patch** of Translations or Command Line (`x.y.z`, z > 0) | No: the gate binds it ("Patch releases" below) | Publish on its own; runic-sdk adopts it afterwards |
| A Command Line **minor** | Yes: R1 needs a Translations minor that covers it in the same train | A train (Command Line, then Translations, then runic-sdk) |
| A Translations **minor** | Yes: R1 (Views.Translations' range names the previous minor) and R5 (the template pins it) | A train ("A Translations-then-SDK train") |
| A **breaking** change across repositories | Yes, as for its minor | A train, made smaller by D069.3's one-minor transition |

**A train is unavoidable for every Command Line or Translations minor** while R1 and R5 hold, whether or not it
breaks anything: P-tr and T run against runic-sdk `main`, and `main` still names the previous minor. Translations
0.14.0 was planned as a standalone release and became a 13-hour train on exactly this (PLAN-0.22). D069.3's
transitions (the upstream adds the new name and keeps the old one `[Obsolete]` for one minor) still help: they keep
the train's runic-sdk candidate to a version and lock change instead of code adaptations, and they let runic-sdk
move off the old names in an ordinary pull request afterwards. They do not remove the train.

**A patch needs no train** (D077.23): it publishes like any release, P-tr (or T) passes against runic-sdk `main`
through the patch path, and runic-sdk adopts it in an ordinary pull request.

### Patch releases

For an upstream patch, a Translations or Command Line release `x.y.z` with z > 0, the gate does not require runic-sdk
`main` to pin the candidate first (D077.23, which amends R5 for patches; `eng/train/patch.mjs`):

- **Translations.** When the packed template pins an earlier patch of the candidate's line (the 0.15.1 case:
  runic-sdk `main` pins 0.15.0, the candidate is 0.15.1), the gate binds the candidate into each template application
  it generates (S3, S4, S5), in its own scratch directory: the `Runic.Translations` and `.Build` pins of the project,
  `dotnet-runic-translations` in `.config/dotnet-tools.json`, and the Translations npm packages in the frontend's
  `package.json` and `package-lock.json`, with the candidate archive's integrity (or npm's, for a published patch),
  before the usual binding of the lock to the candidate registry. Every scenario then builds, tests and runs the
  candidate. S6 accepts the earlier patch in the packed template's pins and notes it.
- **Command Line.** The template pins no Command Line version; the tools bundle `Runic.CommandLine` privately. S6
  accepts a bundled earlier patch of the candidate's line and notes it. S1, S3 and S7 build against the candidate.
- **Not bound:** a minor (R5 holds; a train), a pin on another line or later than the candidate, a prerelease, and
  row P-sdk: runic-sdk's own publish needs its template to pin what its `Directory.Packages.props` builds against.
- The result (`train-result.json`, `patchBinding`) and the summary say what was bound.

After the patch publishes, runic-sdk adopts it in an ordinary pull request (`Directory.Packages.props`, lock files,
template pins and locks), whose CI and the next P-sdk check R5 as before. A security patch is adopted promptly, with a
runic-sdk patch release when the template pins the affected version exactly; other patches are adopted by the next
regular release (D077.23).

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
   `main` run at or after it, which will be the train candidate's merge queue run once it merged.
3. **runic-sdk's train candidate** (one pull request): `Directory.Packages.props` names the Translations version
   with `TrainCommit=` the frozen Translations `main` commit from step 1; the adaptations to the break; the lock
   files written with the feed. Its CI uses the train feed and uploads the `train-feed` artifact. Merge it.
4. **A green runic-sdk `main` run that includes it: its merge queue run** (D077.22). The candidate changes a
   `TrainCommit`, so its queue entry runs every Windows and macOS leg and is never reused from its pull request run
   ([eng/ci/README.md](../ci/README.md), "Merge queue"). When the queue merges it, that run is the green `main` run;
   nothing waits for a push run, and the queue need not stop. Only when no queue run of it passed (an admin merge)
   dispatch `ci.yml` on `main` once (`gh workflow run ci.yml -R Runic-Artifex/runic-sdk --ref main`) and let it
   finish.
5. **Translations dry run, then publish** (`publish-preview.yml` on the frozen commit; the dry run first). Its P-tr
   gate resolves `sdk: run@<gate SHA>` to that green run (the candidate's queue run, or a later one that includes
   it), and before building anything **checks that Translations
   `main` did not move**: the runic-sdk run's `train-feed.json` names the Translations run and commit its feed
   served; the gate's Translations input (the CI run of the commit being published) must be that commit, or, when
   `main` moved, every package the feed served must be byte-identical in the candidate (same SHA-256; with H1's
   reproducible archives a commit that changed no package input passes). Otherwise the gate fails at once with
   "main moved since runic-sdk's train candidate used it" and names both runs: publish the commit the candidate
   used, or move `TrainCommit` and rebuild runic-sdk's candidate. A runic-sdk run without a train feed (no train)
   skips the check.
6. **Feed exit** right after the publish, as soon as nuget.org and npm serve the packages ("Train feed", "Once
   Command Line and Translations are published"): drop `TrainCommit`, delete the cached candidates, rewrite the lock
   files against nuget.org (`eng/ci/train-feed.mjs exit`, then `bun run lock:nuget` and `exit-report`). Until it
   merges every runic-sdk pull request still builds on the feed.
7. **Unfreeze Translations `main`** after its publish. Its gate pin stays valid; move it to runic-sdk `main` with
   the next Translations change that needs a newer gate.
8. runic-sdk publishes from the feed exit's commit or later (row P-sdk, `published:` Translations).

A Command Line train adds the same steps before Translations (row T: `translations: run@<Translations commit>`,
`sdk: run@<gate SHA>`), and the movement check covers Command Line's input the same way.
