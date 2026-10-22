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
is not evidence that the other operating systems passed. Intel macOS is excluded.

## Native jobs per runtime identifier

The `native` matrix (`nativeLegs` in `eng/ci/platforms.mjs`) runs on `linux-x64`,
`linux-arm64` (`ubuntu-24.04-arm`), `win-x64`, `win-arm64` (`windows-11-arm`) and
`osx-arm64`, the last three when planned; `native-musl` runs
`linux-musl-x64` and `linux-musl-arm64`. These are the CI-verified RIDs of
[`eng/support.json`](../support.json), and `eng/support.test.mjs` keeps them equal.
Matrix entries with `cswebui: false` (`win-arm64`) skip the CS-WebUI window probes,
because CS-WebUI ships no native library there; `Runic.Platform.Administration.Windows`
and the IDE reload check run on Windows x64 only. So does the notification-area status
icon: the `windows-11-arm` image's Explorer refuses `Shell_NotifyIcon(NIM_ADD)` with
`E_FAIL`, also from a plain P/Invoke outside Runic. The WPF job runs its hosts on
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
  its input/output level. To update NVDA, change the version and hash in the script;
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
narrow its own plan; changes to the planner or `eng/workspace.json` are unowned and run
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
| `Runic.Desktop.Gtk4`, its smoke, `Runic.Platform.Linux*` and their tests, `eng/portals` | Linux only |
| `Runic.Desktop`, its tests and fixtures, `Runic.Platform`, `Runic.Platform.Runtime` and its tests, the WebView smoke, the accessibility probe, `eng/build/desktop.props`, `eng/branding/desktop`, the template's packaging recipes, `ApplicationIdentity.cs` and icons, the packaging probe | all three |
| `Runic.Application.Views`, `.Desktop` and `.CsWebUi`, `examples/first-window`, the CS-WebUI window probes, `Runic.Application.Testing` and its tests | win-x64, osx-arm64 |
| `Runic.Platform.Windows*` and their tests, the elevation smoke, `windows-notifications` | win-x64, win-arm64 |
| WPF hosts and their tests, `Runic.Navigation` and its WPF consumer, the Views ReactiveUI adapters (`Runic.Application.Views.ReactiveUI*`, whose failure bookkeeping the WPF suite checks under WPF's dispatcher), `examples/wpf-*`, the WPF Bridge consumer, `Runic.Application.Views.Codegen*`, `Runic.Application.Views.Dev`, `examples/notes-reactive-views`, Windows administration | win-x64 |
| `Runic.Platform.MacOS*` and their tests, `platform-sandbox` | osx-arm64 |
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
- **Pushes to `main`** run every Linux job and the legs that the diff between the
  previous and the new head needs. Without that diff (a new branch, a force push whose
  old head cannot be fetched), every leg runs. Pushes queue rather than cancel each other.
- **Nightly and manual runs** (`schedule`, `workflow_dispatch`) run everything. They have
  their own concurrency group, so a push cannot replace one that is waiting.
- **Releases** need one successful run on `main` of the released commit that ran every
  Windows and macOS leg ([eng/release/README.md](../release/README.md)).

- `plan`, `build` and `engineering` always run. `build` checks generated files;
  `engineering` lints workflows and runs the contract and Markdown link tests.
- Markdown outside `packages/`, `tools/` and `tests/`, the `*.test.mjs` files the
  engineering job runs, and workflows other than `ci.yml` are read only by those jobs
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
x64 runners' Azure mirror (`azure.archive.ubuntu.com`) is sometimes throttled to tens of
kB/s: the WebKitGTK set (about 90 MB) then took over 30 minutes and failed the job,
while a healthy mirror delivers it in seconds. The script therefore separates the
download from the install:

- `apt-get update` and `apt-get install --download-only` get a time limit per attempt:
  2 minutes for the update, 90 seconds for the first download and 4 minutes for each of
  the two retries. apt's own network timeout is 30 seconds with three retries per file.
  Files completed by a stopped attempt are kept.
- After a failed attempt the script drops the Azure mirror from the runner's mirror list
  (`/etc/apt/apt-mirrors.txt`), leaving the official `archive.ubuntu.com` and
  `security.ubuntu.com`, or replaces the Azure host in the apt sources when there is no
  list. The rest of the job uses those mirrors. The arm64 runners use `ports.ubuntu.com`
  directly, so they only retry.
- The install then runs with `--no-download`.
- The final failure is an `::error` annotation naming the packages, the mirrors of each
  attempt and apt's errors or the file whose download the time limit stopped.

All mirrors are official Ubuntu archives signed by the same key. Signature verification
stays on: apt verifies the signed indexes and every archive's hash, including cached
archives before it uses them.

`.github/actions/apt-install` (the native job's WebView prerequisites, the Desktop
template runtimes, Playwright's Chromium dependencies and flatpak-builder) also caches
the downloaded `.deb` archives in `/var/cache/runic-apt` through the dependency cache, so
only `main` saves them. The key is
`apt-<Ubuntu release>-<arch>-<hash of the package list>[-norecommends]-<runner image version>`;
a new runner image restores the previous image's archives by prefix, and apt downloads
only the versions that changed. `apt-get autoclean` removes archives the current indexes
no longer offer before the cache is saved. Smaller installs inside multi-command steps
call the script directly. `--no-install-recommends` as the script's first argument, or
`install-recommends: "false"` for the action, leaves out recommended packages.

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
frameworks as named steps.
Only the final `verify` gate succeeding means all required jobs have passed.

Desktop conformance (`Runic.Desktop.Tests`, a TUnit suite) runs in the native job
on every OS, not in the managed job. It writes Microsoft.Testing.Platform
diagnostics and a TRX report to `artifacts/native-tests`. A case fails after two
minutes, `--timeout 3m` ends the session, and the five-minute Actions step limit
backs up stalls outside the test application. Failure artifacts are retained by
the following `always()` upload step. No crash dumps are requested.

## GitHub reruns

Use GitHub's **Re-run failed jobs** or select one job to rerun after a transient
failure. Jobs are the independent rerun unit; named steps make each failure easy
to locate. Successful build/package artifacts use a run-scoped name and are kept
for 30 days, matching the workflow rerun window. A producer rerun overwrites its
own artifact. Missing or expired artifacts require rerunning their producer too.

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
