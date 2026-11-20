# Run the SDK workflow locally

Local workflow execution is optional and useful for CI debugging. Use
`bun run test <scope>` during normal development and let GitHub run full CI.

`.github/workflows/ci.yml` owns verification. GitHub Actions and local `act` runs
execute the same jobs, composite setup actions, commands and dependency graph.
`eng/run.mjs` supplies build/package primitives; it does not maintain another
verification pipeline. Root `test` and `verify` select focused local checks; they do not launch containers.

## Prerequisites and commands

On Linux, install Nix and a working Docker daemon or rootless Podman socket. The
flake supplies Bun, the patched `act` binary and `actionlint`. For Podman:

```sh
systemctl --user start podman.socket
nix develop
bun run ci --list
bun run ci                          # Entire Linux workflow, including native checks
bun run ci --job managed
bun run ci --job web
bun run ci --job views
bun run ci --job templates --matrix first:react
bun run ci --job templates           # Includes build and package prerequisites
bun run ci --dryrun
```

Arguments pass through to `act`; job names come directly from the YAML. Selecting
one job also executes its dependencies. The default schedules one job group at
a time; matrix entries can run concurrently within that group, as defined in the
workflow (up to four containers). Select a job/matrix entry on a machine with
limited memory or disk space; `--concurrent-jobs 2` increases group concurrency
when sufficient resources are available. Run one SDK invocation per checkout at
a time because act uses stable container names. First-party
commands use Bun. Browser journeys and npm/pnpm compatibility jobs explicitly
install Node and the package managers they need. GitHub JavaScript actions have their own Node
runtime, which is independent of the project's choice of Bun.

The first real run builds `runner.Containerfile` on a digest-pinned Ubuntu 24.04
runner image and downloads actions and dependencies. Later runs reuse the image,
action cache and dependency cache. The launcher uses act's archive-based action
cache to avoid parallel jobs resetting the same action checkout. `--action-offline-mode` prevents fetching new
action code or a missing runner image; it does **not** promise offline execution
of tool installers, NuGet, browsers or package-manager tests. A working internet
connection is required for an uncached run. An existing `GITHUB_TOKEN` is forwarded
only when explicitly exported; the launcher does not retrieve credentials from
`gh` or load local `.secrets`/`.env` files. The workflow never publishes packages.

`DOCKER_HOST` selects an explicit daemon; otherwise the launcher detects the
current user's Podman socket, falling back to Docker. `RUNIC_CONTAINER_ENGINE`
can override the image-build CLI. These are local trusted development runs:
containers use host networking for the loopback artifact/cache servers. They do
not receive a mounted container-daemon socket or a writable checkout bind mount.
The engine's init process reaps orphaned children, as on a hosted runner, so
process-tree termination checks also work inside containers. Translation jobs
install their C++20 conformance compiler explicitly through the workflow.
Do not use this runner for untrusted code with credentials in the environment.

The launcher currently supports Linux with an amd64 runner image. Windows x64 and
macOS Apple Silicon jobs remain native GitHub checks. A passing local Linux run
is not evidence that the other operating systems passed. Intel macOS is not supported.

## Test timeouts

Hosted runners are slower and less even than a developer machine: a cold Chromium or WebView2 start, a GC pause or
a busy neighbour can stall one wait for seconds. `RUNIC_TEST_TIMEOUT_SCALE`, a positive number that defaults to 1,
multiplies the waits of the tests and smokes for a condition, so a slow runner gets more room and a real hang still
fails:

- **Scale only waits for a condition**: until the window connects, a file appears, a frame arrives, a process exits.
  A passing test still ends as soon as the condition holds, so the scale costs nothing when the test passes.
- **Never scale product timing**: a deadline that fires after X, relaunch timing, debounce or interval behaviour.
  Such a test belongs on a fake clock (`TimeProvider`, a virtual clock); one that uses real time is fixed there, not
  given more time. A wait that must *not* end (nothing happens within X) is not scaled either.
- **Never scale performance budgets or benchmarks.**

The helpers are `tests/support/TestTimeouts.cs` (a project opts in with `<RunicTestTimeouts>true</RunicTestTimeouts>`,
`eng/build/test-timeouts.targets`; it compiles into the test project, not into any package) and
`tests/support/timeouts.mjs` for Bun and Node, which `examples/shared/smoke.mjs` re-exports. In .NET use
`task.WaitScaledAsync(limit)`, `process.WaitForExitScaled(limit)`, `TestTimeouts.UntilAsync(condition, limit)` or
`TestTimeouts.Start(limit)` for a polling loop; `TestTimeouts.Scale` and `TestTimeouts.Cancellation` scale a limit
that is handed to an API. In JavaScript use `scaled(ms)`, `startWait(ms, label)` or `withinScaled(promise, ms, label)`;
the smoke helpers' `waitFor` and DevTools commands already scale. The limit stays the unscaled value in the source.

A scaled wait that succeeds only after its unscaled limit reports it: in GitHub Actions as a `::warning::`
annotation titled "Slow test wait" at the waiting line, with the elapsed and the unscaled time, elsewhere as a
`warning:` line. The slowness stays visible on a passing run; a recurring annotation is worth an issue.

CI sets the variable for every job that runs tests: `.github/actions/test-timeout-scale`, which `setup-sdk` runs
first, writes the runner's value from `testTimeoutScales` in `eng/ci/platforms.mjs` unless the job sets its own, and
`native-musl` and the real-portal container receive it with `docker run -e`. The values are 4 on Linux x64 and arm64, 5 on
Windows x64 and 6 on Windows arm64 and macOS. In recent `main` runs the same managed desktop suite took 62–75 s on
linux-x64, 60–61 s on linux-arm64, 58–83 s on win-x64, 68–75 s on win-arm64 and 73–98 s on osx-arm64, so whole
suites vary by up to 1.4×. A single wait varies far more: the win-x64 WPF checks failed at a 20 s WebView connect
whose controller creation alone took 3.8 s against 0.7 s in a passing run, and cold Chromium on Windows never answers
DevTools in some launches (#70).

To reproduce a CI-like run locally, set the CI value and run the focused suite:

```sh
RUNIC_TEST_TIMEOUT_SCALE=4 bun run test tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj
RUNIC_TEST_TIMEOUT_SCALE=4 node examples/first-window/browser-smoke.mjs
```

A failure that goes away with a larger scale is runner slowness, and its passing run prints the "Slow test wait"
warning. A failure at any scale is a real bug or a product-timing test on real time. `bun run ci` (act) sets the
Linux x64 value itself.

## Native jobs per runtime identifier

The `native` matrix (`nativeLegs` in `eng/ci/platforms.mjs`) runs on `linux-x64`,
`linux-arm64` (`ubuntu-24.04-arm`), `win-x64`, `win-arm64` (`windows-11-arm`) and
`osx-arm64`, the last three when planned; `native-musl` runs
`linux-musl-x64` and `linux-musl-arm64`. These are the CI-verified RIDs of
[`eng/support.json`](../support.json), and `eng/support.test.mjs` keeps them equal.
Matrix entries with `cswebui: false` (`win-arm64`) skip the CS-WebUI window probes,
because CS-WebUI ships no native library there; `Runic.Platform.Administration.Windows`
and the IDE reload check run on Windows x64 only. The `windows-11-arm` image starts
its session with Windows' full-screen "Microsoft account" prompt (`WWAHost.exe`) over the
desktop; while it is up, Explorer refuses `Shell_NotifyIcon(NIM_ADD)` with `E_FAIL` and
`DoDragDrop` never starts its loop, also outside Runic (#373). The native and WPF jobs
close it first (`eng/ci/windows-dismiss-oobe.ps1`), so the status icon runs on both
Windows legs. The runner still delivers no `SendInput` to WebView2 on arm64, so the drops
onto and drags out of WebView2 stay unverified there (#391). The WPF job runs its hosts on
`win-x64` and `win-arm64`, and the architecture-independent generator and packed
consumer checks on `win-x64`.

`native-musl` checks out and caches on the runner, then runs
`eng/ci/native-musl.sh` in a digest-pinned Alpine container, because GitHub cannot
run JavaScript actions in Alpine containers on arm64 runners. As root, the script
installs Alpine's GTK 3 and WebKitGTK 4.1 and the SDK from `global.json`, then runs
the WebView smoke with JIT and NativeAOT and the Linux platform, portal, polkit and
runtime checks as a user with the checkout owner's uid, as on the glibc runners. The keyboard driver stays on the glibc jobs: its zoom checks read
`devicePixelRatio`, which includes the page zoom in Ubuntu's WebKitGTK 2.52 but not
in Alpine's 2.48. WebKit's
bubblewrap sandbox stays enabled: the container runs without Docker's seccomp filter,
and the runner allows unprivileged user namespaces. act provides no container engine
inside its jobs, so `bun run ci` skips that step; run it directly instead:

```sh
podman run --rm --init --userns=keep-id --user root --security-opt seccomp=unconfined \
  --shm-size 1g -v "$PWD:$PWD" -w "$PWD" -e NUGET_PACKAGES="$PWD/.cache/nuget" \
  <ALPINE_IMAGE from ci.yml> sh eng/ci/native-musl.sh linux-musl-x64
```

The container writes its build outputs into the checkout's `bin`, `obj` and `artifacts`.

The GTK4 smoke steps start their private session bus inside `xvfb-run`
(`xvfb-run -a dbus-run-session -- …`). Services that the bus activates, such as Ubuntu's
xdg-desktop-portal-gtk, inherit the bus's environment rather than the client's; with the
bus outside the X display they exited with "cannot open display" on each activation.

## Display scaling and appearance evidence

The native job changes the runner's own display and preferences to check W315-017
and W315-011, and restores them before the step ends:

- Windows (`--dpi` mode of the WebView smoke, `RUNIC_TEST_DISPLAY_SCALING=1`) raises
  the virtual display to 1920x1080 and switches the scale to 150 % and 175 % while the
  WebView2 window is open, with JIT and NativeAOT. The undocumented
  `SPI_SETLOGICALDPIOVERRIDE` selects the scale step without signing out;
  `tests/native/Runic.Desktop.WebViewSmoke/windows-display-scaling.ps1` does the same
  through Windows Settings for manual runs.
- macOS (`eng/ci/macos-appearance.sh`) writes the accent colour, Increase contrast and
  Reduce motion before each test process starts and switches dark mode through System
  Events while one runs.
- Linux (`eng/ci/real-portal.sh`) runs the published portal tests against the real
  xdg-desktop-portal and its GNOME backend in a digest-pinned Fedora container, because
  Ubuntu 24.04's portal does not publish the accent colour or reduced motion. Its
  package download is bounded and cached ("Fedora packages" below).

Hosted runners have one virtual monitor, and the macOS runner offers no HiDPI mode,
so moving a window between monitors with different scales and macOS scaling need
real hardware.

## Screen reader speech

The `screen-readers` job (`screenReaderLegs` in `eng/ci/platforms.mjs`) runs the
accessibility fixture with a screen reader and checks what it says, as the native
job's `osx-arm64` leg does with VoiceOver: NVDA on `windows-2025` (WebView2) and Orca
on `ubuntu-24.04` (WebKitGTK, GTK 3 and GTK 4, under Xvfb on a private session bus).
`Runic.Desktop.AccessibilityProbe --screen-reader nvda|orca` starts the screen reader
before the page opens, acts on the page through the accessibility API and quits it;
`Expectations.cs` lists what each one is known not to say. The receipts and the
screen reader's own logs are the `screen-reader-<reader>-<run>` artifact.

- **NVDA.** `eng/ci/windows-nvda.ps1` downloads the NVDA launcher pinned by version
  and SHA-256, requires NV Access's Authenticode signature and creates a portable copy
  in `RUNNER_TEMP`. The probe gives it its own configuration (no welcome dialog, update
  check or sounds) and the silence synthesizer, and reads the speech that NVDA logs at
  its input/output level. The speech checks change values and the list selection with
  the keys an NVDA user presses (`SendInput`), since a UI Automation action makes WebView2
  activate its own window and NVDA read the page again (Runic.Desktop README,
  "Accessibility"). To update NVDA, change the version and hash in the script;
  NV Access publishes the launcher's SHA-1 at
  `https://api.nvaccess.org/nvdaUpdateCheck?versionType=stable`.
- **Orca.** Ubuntu's `orca` package. The probe runs Orca with private XDG directories
  and a Speech Dispatcher configuration whose `sd_generic` output module appends each
  utterance to a file instead of synthesizing it (audio goes to ALSA's null device).
  To run it locally, use an isolated session, never the desktop's own:
  `GDK_BACKEND=x11 GSETTINGS_BACKEND=memory dbus-run-session -- xvfb-run -a dotnet run
  --project tests/native/Runic.Desktop.Gtk4.Smoke -c Release -- --accessibility gtk4
  --probe dotnet <probe.dll> --screen-reader orca --receipt artifacts/screen-reader/gtk4.json`.

Both legs run for changes to `Runic.Desktop`, `Runic.Desktop.Gtk4`, the WebView and
GTK 4 smokes and the probe (`screenReaderPaths`), and with every job; NVDA only when
`win-x64` is planned. They are not release jobs (`releaseJobs`). Narrator stays a manual check.

## Affected-only pull requests

The `plan` job runs `eng/ci/plan.mjs`. On a pull request it diffs GitHub's merge
commit against its base parent and maps each changed path to its component in
`eng/workspace.json`, adding every component that depends on it. The base branch's
planner and component map make that decision (`--affected`), so a pull request cannot
narrow its own plan; a change to `eng/workspace.json` is a shared input and runs
everything. The pull request's checkout then enumerates the suites (`--plan`). Scheduled
and manual runs, local `act` runs and an unavailable diff or base result plan every job.

A pull request is planned against the base it was opened or last updated on. Every
push to `main` runs every Linux job, so a change that only breaks in combination
with newer `main` commits is caught there.

### Windows and macOS legs

The Linux legs of `native`, `native-musl` and `packaging` run whenever their job runs.
The Windows and macOS legs (`win-x64`, `win-arm64`, `osx-arm64` of `native`, `wpf` and
`packaging`; `eng/ci/platforms.mjs`) run only for the platforms the changed files
need. The base checkout's rules decide that too, so a pull request cannot drop its
own legs; a base planner without platform rules plans every leg. The first matching
rule applies to each file:

| Changed files | Legs |
| --- | --- |
| Markdown files (`*.md`), including package READMEs, except the analyzers' `AnalyzerReleases.*.md` (compiler inputs) | Linux only |
| `Runic.Desktop.Gtk4`, its smoke, the manual global menu check, `Runic.Platform.Linux*` and their tests, `eng/portals` | Linux only |
| `Runic.Desktop`, its tests and fixtures, `Runic.Platform`, `Runic.Platform.Runtime` and its tests, the WebView smoke, the accessibility probe, `eng/build/desktop.props`, `eng/branding/desktop`, the template's packaging manifests, `ApplicationIdentity.cs` and icons, `dotnet runic package` (`tools/dotnet-runic/Packaging`), the packaging probe | all three |
| `Runic.Application.Views`, `.Desktop` and `.CsWebUi`, `examples/first-window`, the CS-WebUI window probes, `Runic.Application.Testing` and its tests | win-x64, osx-arm64 |
| `Runic.Platform.Windows*` and their tests, the elevation smoke, `windows-notifications` | win-x64, win-arm64 |
| The template's WebView2 script-debugging launch profile (`Properties/launchSettings.json`) and VS Code `launch.json`, the `dev-live-assets` fixture that checks them against WebView2, `dotnet runic dev`'s session file and contract lines (`DevSession.cs`, `DevApplication.cs`), and the tool's files that start programs or choose how (npm is `npm.cmd` on Windows: `CommandRunner.cs`, `ExecutableSearch.cs`, `JavaScriptPackageManager.cs`, `RunningProcess.cs`, `HostProcessController.cs`, the Vite and Angular servers; `windowsProgramStartPaths`), their Windows tests and `dev-npm-smoke.mjs`. The tool's other files plan no Windows leg; `plan.test.mjs` fails if a tool file other than `CommandRunner.cs` starts a process itself | win-x64 |
| WPF hosts and their tests, `Runic.Navigation` and its WPF consumer, the Views ReactiveUI adapters (`Runic.Application.Views.ReactiveUI*`, whose failure bookkeeping the WPF suite checks under WPF's dispatcher), `examples/wpf-*`, the WPF Bridge consumer, `Runic.Application.Views.Codegen*`, `Runic.Application.Views.Dev`, `examples/notes-reactive-views`, Windows administration | win-x64 |
| `Runic.Platform.MacOS*` and their tests, `platform-sandbox`, the sandbox probe | osx-arm64 |
| Nix, `flake.*`, `eng/release`, `eng/verify-packages.mjs`, the solutions, the shipping-project generator (unowned, neutral) | Linux only |
| any other unowned file: `ci.yml`, `eng/ci`, `.github/actions`, `global.json`, `Directory.*`, `eng/Versions.props`, `eng/build`, `bun.lock`, `eng/workspace.json`, `eng/support.json` | all three |
| everything else: web packages, templates, assets, the Navigation ReactiveUI adapters, examples, documentation | Linux only |

A job whose legs are all skipped, such as `wpf` for a Linux-only change, is on the
plan's skip list. The `verify` summary names the planned legs and, for each, the first
file that needed it.

Other runs choose legs this way:

- **`ci:all-platforms` label.** A pull request with this label runs every leg. Add it
  when the platform effect of a change is unclear, for example a change to shared
  managed code that a Windows or macOS host calls. The plan reads the labels when it
  runs and the label does not start a run, so after adding it push again or use
  **Re-run all jobs**. If the labels cannot be read, every leg runs.
- **Merge queue entries** (`merge_group`) plan like an unlabelled pull request from
  their merge group's diff; see [Merge queue](#merge-queue).
- **Pushes to `main`** run every Linux job and the legs that the diff between the
  previous and the new head needs. Without that diff (a new branch, a force push whose
  old head cannot be fetched), every leg runs. Pushes queue rather than cancel each other.
- **Nightly and manual runs** (`schedule`, `workflow_dispatch`) run everything. They have
  their own concurrency group, so a push cannot replace one that is waiting.
- **Releases** need one successful run on `main` of the released commit that ran every
  Windows and macOS leg ([eng/release/README.md](../release/README.md)).

- `plan` always runs. `build` and `engineering` run in every plan except a merge queue
  entry's reuse of a passed pull request run (see [Merge queue](#merge-queue)). `build`
  checks generated files; `engineering` lints workflows and runs the contract and Markdown
  link tests.
- Markdown outside `packages/`, `tools/` and `tests/`, the `*.test.mjs` files the
  engineering job runs, workflows other than `ci.yml` and the train gate (`eng/train`,
  which only `train-compat.yml` and its engineering tests run) are read only by those jobs
  (`engineeringOnly` in `eng/run.mjs`; `bun run affected` uses the same rule).
- A path that no component owns, such as `Directory.Build.props`, the shared
  `eng/build/application.{props,targets}`, other `eng/` tooling or
  `.github/actions/`, runs everything.
- Managed groups and web packages run when a component owning one of their suites
  is affected. The `managed` job runs each planned group, and the `web` job each planned
  web package, as its own named step; a step also runs after another one failed, so one
  run reports every failure. When React, Vue, Svelte, Angular, views-effect,
  vite-plugin-runic or sveltekit is planned, the `web` job also runs their tests at
  the lowest framework (or Vite) version each peer range accepts
  (`tests/web/lowest-peers`). The `web` job's Svelte checks and npm framework consumers
  follow `svelte`; `views` follows `examples`; package consumers and templates run for
  any affected component.
  Native jobs follow `desktop`, `platform`, `application`, `examples` and
  `administration-windows`, with the legs above; the screen reader legs follow
  `screenReaderPaths` (see [Screen reader speech](#screen-reader-speech)).

Skipped jobs report `skipped`, so no check stays pending. The required `verify` job
(`eng/ci/gate.mjs`) accepts `skipped` only for jobs in the plan's skip list; a
failed plan fails it. The engineering tests check that the component `dependsOn` graph
covers every ProjectReference, workspace npm dependency, MSBuild import and
cross-directory item include (with an allow-list for generated output, restored
packages and external checkouts), so a new reference cannot silently escape the plan. The planner tests check
each platform rule against a file it names, and that every rule still matches tracked
files. Preview the plan for a branch with
`bun eng/ci/plan.mjs --files $(git diff --name-only origin/main...)`. To run every
job for a pull request, start the workflow manually on its branch; to run every leg of
its planned jobs, add the `ci:all-platforms` label.

### File ownership

Every tracked file is in one of four groups, and `plan.test.mjs` fails on a file in
none, listing it, so a new file cannot silently plan every job and leg (I11: in 0.21
an `eng/` script nobody owned ran a whole plan):

- **Owned** by a component in `eng/workspace.json`. The CI component (`ci`) owns the
  planner (`plan.mjs`, `platforms.mjs`), the `verify` gate, the local runner
  (`local.mjs`, `snapshot.mjs`, `runner.Containerfile`, the `act` patch), the
  fixtures, the flake report, the reliability and dependency-audit tooling,
  `eng/json-schema.mjs`, `CODEOWNERS` and `.gitignore`: files that only the
  always-run jobs (plan, build, engineering, verify), other workflows or the
  coordinator read. Its checks are the engineering job's, so a change to it alone
  runs no skippable job and no Windows or macOS leg. A change to a leg's runner or
  matrix values in `platforms.mjs` takes the `ci:all-platforms` label. A CI script
  that a platform step runs belongs to that step's component, with a platform
  rule: the macOS scripts and `hdiutil.sh` plan osx-arm64, the Windows ones their
  Windows legs, `windows-nvda.ps1` the NVDA leg, `real-portal.sh`,
  `native-musl.sh` and `packaging-rpm.sh` Linux only.
- **Engineering-only** (`engineeringOnly` in `eng/run.mjs`): documentation, the
  engineering tests (`eng/ci/*.test.mjs` and the other engineering directories),
  other workflows and `eng/train`. They affect no component.
- **Neutral** (`neutralPaths`): unowned files no Windows or macOS job reads (Nix,
  release tooling, the solutions, the Linux-only CI helpers). Every Linux job.
- **Global** (`globalPaths`): shared inputs of every job, such as `global.json`,
  the MSBuild defaults, lock files, `ci.yml` and its actions, `eng/build`,
  `eng/run.mjs` and the shared CI scripts. Every job and leg.

Give a new file an owner where one fits; list it as neutral or global only when it
really is a shared input.

## Merge queue

`main`'s ruleset can require pull requests to pass the merge queue. Queue a pull
request with **Merge when ready** in the pull request page, or from the command line:

```sh
gh pr merge <number> --squash --auto
```

`--auto` queues it once its own checks pass; without the merge queue it enables
auto-merge instead. GitHub then creates a temporary branch
`gh-readonly-queue/main/pr-<number>-<sha>` holding `main` (with the entries ahead of
it) plus the pull request, and runs `ci.yml` on it as a `merge_group` event. The
`verify` check of that run decides whether the entry merges.

- **What runs.** The plan diffs `merge_group.base_sha..head_sha`, the merge group's
  parent (`main`, or the entry ahead of it in the queue) against the queued commit,
  and the base checkout's planner and rules decide what is affected, as on a pull
  request. Labels are not read, so a queue entry runs the jobs and the Windows and
  macOS legs its diff needs, even when the pull request has `ci:all-platforms`; that
  label's run already happened on the pull request. Without a usable diff the entry
  runs everything.
- **Reuse.** When `verify` passes on a pull request from this repository, it uploads a
  marker artifact `ci-passed-<base>-<tree>` (kept 7 days) naming its merge commit's base
  parent and tree. A squash entry on the same base has the same tree, so it tests identical
  code: its plan finds the marker, checks that the run it belongs to is a `ci.yml`
  `pull_request` run from this repository whose `verify` job succeeded, and then skips every
  job, `build` and `engineering` included, so `verify` passes within about a minute. When
  `main` moved, another entry is ahead, the pull request changed since, or any lookup fails,
  the entry plans as above. The push run on `main` after the merge and the release rules
  are unchanged.
- **Concurrency.** Each entry has its own ref and so its own concurrency group: queue
  runs do not cancel each other, pull request runs or pushes. When GitHub removes an
  entry, for example because one ahead of it failed, it rebuilds the later entries
  and starts new runs for them.
- **After the merge.** The queue fast-forwards `main` to the tested commit, which
  starts the usual push run (every Linux job and the legs the `before..after` diff
  needs). Releases still take only push, scheduled or manual runs on `main`
  ([eng/release/README.md](../release/README.md)); a queue run does not count.
- **Flakes.** A failed entry leaves the queue and its pull request stays open. After a
  transient failure, queue the pull request again (the same command or button);
  rerunning the failed queue run's jobs does not put it back, because its temporary
  branch is gone. Fix a reproducible failure with a new commit to the pull request.

Preview a queue entry's plan with
`bun eng/ci/plan.mjs --files $(git diff --name-only origin/main...)`, as for a pull request.

## NuGet lock files

Projects in `RunicSdk.Core.slnx` commit `packages.lock.json`
(`eng/build/nuget-lock.targets`), and CI restores with `--locked-mode`. Run
`bun run lock:nuget` after changing references, package versions or the SDK pin.
Publishing with a RuntimeIdentifier or NativeAOT changes the package graph of every
referenced project, so those restores are not locked; `Directory.Packages.props` still
pins their direct versions. The `core` NuGet cache key hashes the lock files and package
props; native jobs use a separate `native` key, and `native-musl` a `native-musl-<rid>` key. Each falls back to the latest cache for
the same OS and architecture. Web and Svelte consumer jobs skip NuGet entirely.

Every dependency cache (Bun, NuGet, Playwright, Flatpak, Ubuntu package archives) goes through
`.github/actions/dependency-cache`: runs on `main` restore and save, while pull requests and
other branches only restore. Pull request entries would otherwise fill the repository's
cache quota and evict `main`'s caches, which every run restores from.

## Ubuntu packages

`eng/ci/apt-install.sh <package>...` installs Ubuntu packages on Linux runners. The
x64 runners' mirror list (`/etc/apt/apt-mirrors.txt`) starts with the Azure mirror
(`azure.archive.ubuntu.com`) on some runner images and with an EC2 mirror
(`us-east-2.ec2.archive.ubuntu.com`) on others, followed by `archive.ubuntu.com` and
`security.ubuntu.com`. Either first mirror can crawl. The Azure one has been throttled
to tens of kB/s before, which is why an earlier version simply dropped it. In run
44255182128 (#1129), with nothing cached, it was the other way round:

| Job | First mirror | Size | Time | Rate |
|---|---|---|---|---|
| Packaging / deb | Azure | 53.8 MB | 17 s | 3167 kB/s |
| Templates / vue+angular | Azure | 99.6 MB | 12 s | 8301 kB/s |
| Packaging / appimage | EC2 | 53.8 MB | 465 s | 116 kB/s |
| WebKitGTK debugging / js-debug | EC2 | 99.6 MB | 458 s | 217 kB/s |
| Screen reader / orca | EC2 | 113.3 MB | 1169 s | 97 kB/s |
| Web packages | EC2 | 125.1 MB | budget used up | about 100 kB/s |

So no host is assumed slow; the script measures them. It separates the download from
the install:

- `apt-get update` gets 2 minutes per attempt. `apt-get install --print-uris` then lists
  the archives to fetch.
- **Probe.** Before the first download, each candidate mirror gets a ranged GET
  (`curl --range`) of the first 2 MB (`APT_PROBE_BYTES`) of the largest archive to
  fetch. The probes run in parallel, each at most 8 seconds (`APT_PROBE_SECONDS`).
  The candidates are the runner's own Ubuntu mirrors plus `azure.archive.ubuntu.com`
  and `archive.ubuntu.com`, deduplicated; only `*.ubuntu.com` hosts are used. The
  mirrors are ordered by measured rate, and a host whose probe fails or delivers
  nothing goes last. One log line lists host and rate. Nothing is probed when there is
  nothing to fetch.
- **Applying the order.** For the mirror list, the script rewrites it with explicit
  `priority:` tags; apt otherwise picks among untagged mirrors at random. For plain
  sources, it replaces the archive host and leaves the security pocket's entry alone,
  then refreshes the indexes, since apt keeps them per host. The rest of the job uses
  the last order.
- **Stop rules.** `apt-get install --download-only` runs as long as it makes progress,
  and is stopped in two cases:
  - no growth for 60 seconds (`APT_STALL_SECONDS`) in the archives directory (finished
    `.deb` files and apt's `partial/` downloads);
  - while an untried mirror is left, an average rate below 1 MB/s (`APT_RATE_FLOOR`,
    in bytes per second) after 45 seconds (`APT_RATE_WINDOW`).
- **Next attempt.** It uses the fastest untried mirror. A mirror stopped for its rate
  keeps the rate it measured, and a stalled one goes last. Completed and partial files
  are kept and resumed.
- **Budget.** All attempts (at most five) share a 20-minute download budget
  (`APT_DOWNLOAD_SECONDS`). The last remaining mirror has no rate floor, so a slow but
  steady one still finishes: the Orca job's 113 MB took 1169 seconds at 97 kB/s.
- apt's own network timeout is 30 seconds with three retries per file.
- The arm64 runners use `ports.ubuntu.com`, which has no alternative. They skip the
  probe and the rate floor, and only retry.
- **Logging.** Each attempt logs what it has to fetch and, at the end, the bytes it
  fetched, its average rate and the bytes still to fetch. A running download logs its
  progress every minute.
- The install then runs with `--no-download`.
- **Failure.** The final failure is an `::error` annotation naming the packages. For
  each attempt it gives the mirrors, bytes, rate and remaining bytes, and why the
  attempt stopped (no progress, the rate floor or the budget), with apt's errors or the
  file being fetched.

All mirrors are official Ubuntu archives signed by the same key. Signature verification
stays on: apt verifies the signed indexes and every archive's hash, including cached
archives before it uses them. `eng/ci/apt-install.test.mjs` runs the script against a
stubbed `sudo`, `apt-get` and `curl`. It covers:

- a probe that picks the fast mirror;
- a mirror that probes well but downloads below the floor;
- a slow last mirror that keeps delivering;
- a stalled mirror;
- plain sources;
- `ports.ubuntu.com`;
- the budget;
- `--resolve`'s hash of the package versions, and the install reusing its indexes.

Every workflow step installs through `.github/actions/apt-install` (the native job's
WebView prerequisites, GNOME Keyring and gio, the Desktop template runtimes, Orca,
Playwright's Chromium dependencies and the packaging tools); `contracts.test.mjs` fails
on a direct call of the script. The action caches the downloaded `.deb` archives in
`/var/cache/runic-apt` through the dependency cache. On `main` it saves them right after
the install (`defer-save`, then `save-now`) rather than at the end of the job, so a later
failure in the job no longer discards a slow download; other branches only restore. It
does not save after an exact hit or when every package was already on the image. The key
is `apt-<Ubuntu release>-<arch>-<hash of the package list>[-norecommends]-<runner image version>-<package versions>`.

The last part is a 16-character hash of the archive file names (`name_version_arch.deb`)
the packages need (#1131). Without it, an entry was never saved again once its key
existed, so a newly published package version (webkitgtk 2.52.6 and mesa in November
2026, 50–125 MB per job) was downloaded on every run until the runner image changed. The
action's first step, `apt-install.sh --resolve`, runs `apt-get update` and lists the
archives with `apt-get install --download-only --print-uris` against an empty archive
directory, so archives restored earlier in the job do not change the hash. The cache is
restored after it; the install's first attempt reuses those indexes (`APT_UPDATED`)
instead of updating again, so the extra cost is the listing. A new package version
therefore gets a new entry. The restore falls back to the same image's latest entry, then
to any image's, and apt downloads only the versions that changed. When the versions
cannot be resolved (`apt-get update` fails), the key ends in `unresolved`, the
install updates the indexes itself and nothing is saved. `apt-get autoclean` removes
archives the current indexes no longer offer before the cache is saved, so each entry
holds one package-version set and superseded archives do not accumulate. `--no-install-recommends` as the script's first
argument, or `install-recommends: "false"` for the action, leaves out recommended packages.

No workflow step runs `apt-get` directly. `.github/actions/install-browser` does not use
Playwright's `install --with-deps`, which runs `apt-get update` and `apt-get install`
without a time limit or mirror fallback. It lists the packages Playwright needs for the
runner's Ubuntu release that the image lacks (`playwright-core install-deps --dry-run`,
which simulates the install against the image's package indexes), installs them through
the action without recommended packages, as Playwright does, and then downloads Chromium
with a 5-minute limit per attempt. When the image already has every package, the apt
step is skipped.

## Fedora packages

The real GNOME portal check (`eng/ci/real-portal.sh`, "Display scaling and appearance
evidence") installs xdg-desktop-portal, its GNOME backend and their dependencies (177
packages, about 145 MB) with dnf in the digest-pinned Fedora container. A slow Fedora
mirror has held that download until the step's 10-minute limit with no output, because
`dnf -q` prints nothing while downloading and dnf only drops a mirror below 1000 B/s.
The script therefore:

- Downloads with `dnf install --downloadonly` in two time-limited attempts (2 and 2.5
  minutes; a healthy mirror needs well under one) through `eng/ci/retry.sh`, with
  `minrate=100k`: a mirror slower than 100 kB/s for 30 seconds is dropped for the next
  mirror in Fedora's metalink. Packages completed by a stopped attempt are kept.
- Then falls back to the restored package cache alone (`--cacheonly`), whatever the age
  of its metadata, with a warning annotation.
- Installs with `--cacheonly`, without network access.
- Fails with an `::error` annotation naming each attempt, how many packages it had
  downloaded and dnf's errors, well before the step's limit.

Signature checking stays on: dnf verifies every package's checksum against the
repository metadata and its OpenPGP signature against the Fedora key in the image,
including cached packages. A cached package that does not match is downloaded again.

The native job mounts `/var/cache/runic-dnf` as the container's `/var/cache/libdnf5` and
keeps it in the dependency cache (only `main` saves). The key is
`dnf-<hash of the image reference>-<arch>-<hash of real-portal.sh>-<year-month>`, restored
by prefix, so `main` saves current updates once a month. Before the cache is saved the
script removes the packages it did not install and dnf's generated `solv` files, which
keeps it near 200 MB per architecture. The image itself is pulled with two 90-second
attempts.

Locally, add `-v <directory>:/var/cache/libdnf5` to the `podman run` command in the
script's header to reuse downloaded packages.

The packaging job's `rpm` leg installs the probe's rpm the same way with
`eng/ci/packaging-rpm.sh` (WebKitGTK and its dependencies), in its own cache entry
`dnf-packaging-<hash of the image reference>-<arch>-<year-month>`.

## Packaging tools

The packaging job's Linux legs install `flatpak-builder`, `rpm`, `appstream` and
`desktop-file-utils` through the bounded apt path. The `appimage` leg downloads
`appimagetool` and the AppImage type2 runtime from their GitHub releases, pinned by
version and SHA-256 in `.github/workflows/ci.yml`, so `dotnet runic package appimage`
downloads nothing. To update them, change the URLs and hashes there; GitHub shows each
release asset's SHA-256. The `flathub` leg builds `tests/fixtures/flathub`, an
independent project with its own lock files, from a temporary git repository with
`dotnet runic package flathub --build`; its Flatpak cache entry
(`flatpak-gnome-50-flathub-…`) also holds the .NET and Node.js SDK extensions. The
manifest pins the runtime to the fixture's lock file, so a newer .NET SDK needs no new
lock files; to change a dependency, restore them in a copy outside the repository
(`dotnet restore -p:PublishAot=true`, then `dotnet restore`) and commit them.

## Source snapshots and artifacts

Each real invocation freezes the current tracked and nonignored untracked files,
including uncommitted edits, into a temporary checkout. Every job copies that
same snapshot. You can continue editing without changing a run's inputs. Build
outputs stay in containers; downloaded/uploaded artifacts remain under the printed
`artifacts/act/<run>/` path. The snapshot is removed after the run; failed containers are also removed by
default (`--rm=false` keeps them for debugging). Each job checks
that it did not modify source or lockfiles relative to its own starting state.

The build job archives managed `bin`/`obj` and web `dist` outputs in a tarball,
preserving executable bits and symlinks. Consumers validate source digest, commit,
checkout path, OS, architecture, configuration, SDK and archive checksum before
extracting it. Absolute paths in MSBuild intermediates require the same checkout
path. Linux jobs reuse this build; Windows and macOS rebuild native inputs.
Dependency caches are separate from build artifacts and never authorize reusing
an old build for changed source.

The build job packs the package candidates after archiving its outputs, independently
of test completion, and uploads them as `runic-sdk-<run>`. The package-consumer,
template and WPF jobs validate these candidates. The two template lanes each run two
frameworks as named steps. Every template lane installs Chromium and opens each
frontend's production page, served by the Runic Desktop and CS-WebUI projects with
their real Content-Security-Policy, and fails on any policy violation
(`RUNIC_TEMPLATE_BROWSER_CHECK=1`, `tests/templates/check-template-page.mjs`).
Only the final `verify` gate succeeding means all required jobs have passed.

Desktop conformance (`Runic.Desktop.Tests`, a TUnit suite) runs in the native job
on every OS, not in the managed job. It writes Microsoft.Testing.Platform
diagnostics and a TRX report to `artifacts/native-tests`. A case fails after two
minutes, `--timeout 3m` ends the session, and the five-minute Actions step limit
backs up stalls outside the test application. Failure artifacts are retained by
the following `always()` upload step. No crash dumps are requested.

## Flakes

The policy (D069, C10): a merge queue entry that failed only in a known flake is
re-queued without investigation; a failure seen twice gets an issue the same day,
and within the cycle a fix, or a skip that links the issue. `eng/ci/flakes.mjs`
applies it to recent runs. It reads the failed jobs of `ci.yml` (pull requests, the
merge queue, `main`, schedules; every attempt, so a failure that passed on rerun
counts) through `gh api`, keeps the part of each job log that its failed step wrote
(by the step's start and end times, so a test that failed in an earlier
`continue-on-error` step is not the job's failure), and gives each failure a
signature: its failing test (TUnit, bun and node:test failure lines, a probe's
`FAIL name:`, or the test active when a session hit its `--timeout`) and first error
line. When the log names no test, the signature is the failed step and the line that
names its cause: a `##[error]` annotation that says more than the exit code, else the
first compiler, MSBuild, ILC or NuGet error (`error IL2037:`, not the `MSB3077` that
follows it), else a probe's own failure line (a deadline, `HOSTED_WINDOWS_FAILED:`,
`hdiutil: attach failed`, a download out of attempts, an unhandled exception,
PowerShell's thrown message), else the last error line, not counting a test host's
echo of captured output (`StandardError: child-error`) or a wrapper such as
`bash exited with 1`. Paths, numbers and JSON are left out, except exit codes and
numbers inside a word (`exit code 127`, `CS0436`), so one signature on several runs,
jobs or legs is one entry:

```sh
bun eng/ci/flakes.mjs              # the last two days
bun eng/ci/flakes.mjs --since 7d   # or 36h, or a date; --min 3, --json, --repo owner/name
```

It lists the entries seen twice or more that no issue mentions (file one), those that
failed again after their issue closed (reopen it), repeated failures in steps whose
log names no test (read one: a real break or a flake), known flakes (an open issue
covers them), infrastructure failures, fixed ones and first sightings. An issue
covers an entry when it cites one of its runs (an `…/actions/runs/<id>` link), quotes
the words of its error, or names its test together with one of its platforms
(`linux-x64`, `Windows`, `nvda`). An issue that names only the test may be about
another failure of it (#35, a Windows timeout, against #1273 on linux-x64): it is
listed as a weak match and does not make the entry known, fixed or regressed. An entry whose job passed on a rerun of the same commit is marked a flake.
An infrastructure failure is a step's error from a runner or service, not the code: an
Ubuntu mirror out of attempts, `setup-dotnet`'s download, the artifact service, a
runner shutdown, a network reset or a registry's server error. CI retries the
downloads that fail this way: `.github/actions/download-artifact` tries an artifact
download a second time after 30 s, and `setup-sdk` installs a .NET SDK a second time
from an empty directory; each retry leaves an `Infrastructure retry` warning.

For each failed merge queue entry it gives a verdict:

- **re-queue**: every failure is a known flake, an infrastructure failure, or matches
  an issue that was open when it failed or closed less than seven days before (a fix
  that did not hold; reopen the issue too);
- **file-issue**: the other failures are flakes by evidence (passed on a rerun, or
  failed for two or more pull requests or branches) that no open issue names: file or
  reopen the issue the same day, then re-queue;
- **investigate**: a failure is neither, so it may be the change.

Cancelled runs are counted, not classified. A server error on a page retries it with
a smaller page size. It only reads: the coordinator runs it, not a CI job, and it
files, comments on and reruns nothing. `flakes.test.mjs` runs it on recorded API
responses (`fixtures/flakes`) and on failure texts from real job logs.

## Recorded retries

A failed test is retried once, and only a test marked for it (B1 of D077.25): a test of
a real window manager, focus, drag or dialog, of what a real screen reader says, of a
real browser or WebView starting and connecting, or of a wait on real time. The mark is
`[RecordedRetry(RetryReason.Gui)]` (or `Speech`, `BrowserConnect`, `RealClock`) on the
class or method, in a project that sets `RunicFlakyTests` (which compiles
`tests/support/RecordedRetry.cs`, a TUnit `RetryAttribute` with one retry). A security
probe or budget check is never retried: the attribute refuses a test whose method,
class or category names admission, redaction, an origin, a token, a sandbox, a
preflight, a permission, elevation, a budget, a size or memory, and
`contracts.test.mjs` rejects such a mark and any assembly-wide one.

Every retry is recorded. A job that runs these projects starts with
`.github/actions/flaky-tests` (`mode: start`), which names the record file in
`RUNIC_RETRIED_TESTS`; without it nothing is retried. The test appends a JSON line (the
test, its assembly and class, the leg, the reason, the first failure's exception, the
attempt) before the retry. The job's last step (`mode: report`, `if: always()`)
prints a `Flaky test retried` warning for each line, lists them in the job summary and
uploads the file as the artifact `flaky-tests-<job>-<leg>-<attempt>`. A test that
fails its retry too fails the job as before.

`eng/ci/flakes.mjs` reads those artifacts of every run in its window, passed or
failed, and counts each line as a sighting of the test, marked "retried, passed" or
"retried, failed again". So D069's rule still applies to a test that only ever passed
on its retry: seen in two runs without an issue, it needs one the same day. A test
that passed on its retry does not count against a merge queue entry that failed for
another reason. `--no-retries` skips the artifact listings (one request per run) for a
long window.

The scripted journey and native smoke steps (the ci.yml "Exercise …" steps) are not
retried: each is a whole program run with its own waits, and a retry would rerun the
step, not one check.

## Quarantine

A test with an open flake issue can move out of the required checks while it is fixed
(B2 of D077.25, D069's "skip that links the issue" made explicit). `eng/ci/flakes.json`
lists it with its issue:

```json
{ "quarantine": [
  { "issue": 1273, "job": "native", "legs": ["linux-x64"], "project": "tests/dotnet/Runic.Desktop.Tests",
    "class": "BrowserHostTests", "method": "OwnsChromiumProcessProfileRestartAndNaturalExit" }
] }
```

`legs` (runtime identifiers, or a screen reader) is optional: without it the test is
quarantined on every leg of the job. `wrapper` (such as `["xvfb-run", "-a"]`) and
`arguments` (more test-host arguments) are optional too. Only a test in a project that
sets `RunicFlakyTests`, run by a job that starts `.github/actions/flaky-tests`
(`managed`, `native`, `screen-readers`, `wpf`), can be listed.

- **The required steps skip it.** The action's start step sets `RUNIC_QUARANTINE`, and
  `tests/support/Quarantine.cs` skips a listed test on its legs, with the issue in the
  skip reason.
- **It still runs and reports on the same leg.** The action's report step first runs
  each of the job's and leg's quarantined tests on its own (`eng/ci/quarantine.mjs
  run`, `dotnet run --project … --treenode-filter /*/*/<class>/<method>`) in a
  `continue-on-error` step of the same job, so no other runner (or macOS slot) is used.
  A failure is a `Quarantined test failed` warning, a pass a notice. Both go in the job
  summary and the `flaky-tests-…` artifact, and `eng/ci/flakes.mjs` counts a failure as
  a sighting that fails no queue entry.
- **The list is checked.** `quarantine.test.mjs` rejects an entry without an issue, for
  another job, or naming a test that does not exist. `flakes.mjs` lists the
  quarantined tests with their issues' states and names an entry whose issue is closed
  ("remove the entry"). `node eng/ci/quarantine.mjs check --online` does both.
- **A release waits for it.** `publish-preview.yml`'s preflight runs
  `node eng/ci/quarantine.mjs gate`, which fails while the list names any test, open
  issue or not (offline; `--online` adds each issue's state): fix the test, close the
  issue and remove the entry, all within the cycle.

Scripted journey and native smoke steps (the "Exercise …" steps) cannot be listed yet;
a step would need a `continue-on-error` condition from the plan job.

## GitHub reruns

Use GitHub's **Re-run failed jobs** or select one job to rerun after a transient
failure. Jobs are the independent rerun unit; named steps make each failure easy
to locate. Successful build/package artifacts use a run-scoped name and are kept
for 30 days, matching the workflow rerun window. A producer rerun overwrites its
own artifact. Missing or expired artifacts require rerunning their producer too.
An upload that only keeps diagnostics (the "Retain …" steps) sets `continue-on-error`, so an
artifact service error cannot fail a job whose checks passed; an artifact a later job downloads
still fails its producer (`eng/ci/contracts.test.mjs`).

A GitHub rerun uses the original commit and workflow, even after a fix is pushed.
Push the fix to create a new run; that run builds its own inputs. A new local
invocation likewise creates a fresh source snapshot and reruns selected jobs and
their prerequisites. It reuses download caches, not previous test results.

See GitHub's [rerun documentation](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)
and the [`act` usage guide](https://nektosact.com/usage/index.html).

## Maintaining this tooling

Add verification commands as workflow steps or independently rerunnable jobs.
Use the shared setup actions for pinned tools and frozen restores. Managed
executable suites are discovered from `RunicSdk.Core.slnx`; web test suites are
discovered from `eng/workspace.json`. Both matrices come from the plan. Add a new
skippable job to `skippableJobs` in `eng/ci/plan.mjs` with its `if` condition, and
give new projects a component owner; contract tests check both. Keep
focused package/test commands available for quick development without a container.
The engineering job runs actionlint over every workflow; pin remote actions as
described in [dependency review](../dependencies/README.md).

```sh
nix develop --command actionlint
nix develop --command bun test ./eng/ci/contracts.test.mjs
bun run ci --workflows eng/ci/fixtures/artifact-roundtrip.yml
```

The flake patches `act` 0.2.89 with the two artifact-protocol changes from
[upstream PR #6115](https://github.com/nektos/act/pull/6115), revision
`34fc9c523f2ea43ff90f720b4d2b001a00c91dca`: tolerate new optional protobuf fields and
unpadded Azure signatures. This allows the same upload-artifact v7 and
download-artifact v8 actions locally and on GitHub. Remove the patch after updating
to an upstream release containing both fixes and passing the artifact roundtrip
fixture. Do not downgrade workflow actions to accommodate an outdated local tool.
