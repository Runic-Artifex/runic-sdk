# Split CI: test and build the web on Linux, package on Windows

A Runic Desktop application whose CI runs the tests and all web work on Linux
runners, and uses the Windows runner only to package. The web build reaches the
Windows job as an artifact, a **web bundle**, so that job needs neither Node.js
nor a frontend install (since 0.24, D082).

Unlike the other examples, this one is a repository of its own. It uses the
published Runic packages and the `dotnet-runic` tool, and nothing above this
folder applies to it. Copy the folder to the root of a repository and its
workflow runs as it is.

| Path | What it is |
| --- | --- |
| [`src/Acme.App`](src/Acme.App) | A Runic Desktop application with one Window: [`CounterViewModel.cs`](src/Acme.App/CounterViewModel.cs) (CommunityToolkit.Mvvm) and a Vite page in [`Frontend`](src/Acme.App/Frontend), installed with npm from its lock file |
| [`tests/Acme.App.Tests`](tests/Acme.App.Tests) | Headless Window tests (TUnit, `Runic.Application.Testing`) that drive the ViewModel through its generated Bridge |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | The workflow for GitHub Actions and Gitea Actions |
| [`.forgejo/workflows/ci.yml`](.forgejo/workflows/ci.yml) | The same workflow for Forgejo Actions |
| [`ci/check-workflows.mjs`](ci/check-workflows.mjs) | Checks that the two workflow files differ only where they must, then runs `actionlint` if it is installed |
| [`ci/local-run.sh`](ci/local-run.sh) | Runs both halves on one Linux machine |
| [`Directory.Build.props`](Directory.Build.props), [`Directory.Packages.props`](Directory.Packages.props), [`global.json`](global.json), [`.config/dotnet-tools.json`](.config/dotnet-tools.json) | The build settings, the package versions (one `RunicVersion`), the .NET SDK and the pinned `dotnet-runic` |
| `package-smoke.mjs` | runic-sdk's own test of the example against its packed packages; delete it from a copy |

## When this helps

This setup suits a team with many Linux runners and one or a few runners of its
target OS, such as a self-hosted Gitea with four Linux runners and one Windows
runner. Most of a Runic application's build runs on any OS:

| Runs on Linux | Needs the target OS |
| --- | --- |
| The .NET build, the Bridge generator and the TypeScript clients | NativeAOT (`Cross-OS native compilation is not supported.`) |
| The frontend install and build | The MSIX (`makeappx`) and signing (`signtool`) |
| The headless Window tests of the ViewModels | Running WPF or WebView2 code, installing the MSIX |
| A framework-dependent or self-contained `win-x64` publish | |

## The pipeline

```
          push, pull request                       main or a tag
                 │                                       │
     ┌───────────┴───────────┐                           │
     ▼                       ▼                           │
   test                   web-bundle                     │   Linux runners, in parallel
   dotnet test            dotnet runic bundle            │
                          upload the web-bundle artifact │
     └───────────┬───────────┘                           │
                 ▼  needs: [test, web-bundle], if: a push to main or a tag
     ┌───────────┴─────────────────────┐
     ▼                                 ▼
   windows-package                   linux-package
   download web-bundle               download web-bundle
   dotnet runic package windows      dotnet runic package deb
     --frontend-bundle                 --frontend-bundle
   upload the MSIX                   upload the deb
```

The packaging jobs run only after every Linux job passed, and only for pushes
to `main` and tags (`if: github.event_name == 'push'`). Pull requests never wait
for the Windows runner. To package pull requests too, remove the `if:`.

## Build the web bundle on Linux

The `web-bundle` job sets up .NET and Node.js, restores the tool and writes the
bundle:

```sh
dotnet tool restore
dotnet runic bundle --project src/Acme.App --output-directory "$RUNNER_TEMP/web-bundle"
```

`dotnet runic bundle` builds the application in Release. The build generates the
TypeScript clients, installs the frontend with `npm ci` from
`Frontend/package-lock.json`, and builds it with Vite. It then writes the page,
the generated clients and the View fingerprints to the bundle, and prints one
line per project:

```
Acme.App (application): 1 View(s), 2 files, 49.9 KiB, SHA-256 38aa10cc89e9; host desktop, NODE_ENV production
```

The job uploads the folder as the `web-bundle` artifact for 7 days. Its
`web-bundle.json` (format `rax.web-bundle/1`) records each View's fingerprint
from the contract IR, the host, `NODE_ENV`, the lock file's hash, the
`@runic-artifex/views` version, and the .NET SDK and Node.js versions. See
[Write a web bundle](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md#write-a-web-bundle)
and [Building the frontend in another job](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#building-the-frontend-in-another-job).

`dotnet runic bundle` takes the application project, not the solution: the test
project builds the application with `RunicApplicationFrontendBuildEnabled=false`,
and a project that doesn't build its page can't write it to a bundle
(`RUNICBRIDGE092`).

## Test on Linux

The `test` job runs `dotnet test` and starts at once, beside `web-bundle`. The
tests open the Window headless with `RunicWindowTestHost` and run its commands
through the generated Bridge, as the page does, so they need no window, browser
or Windows runner. A frontend test job can download the bundle and copy
`projects/Acme.App/clients/` into `Frontend/src/generated`. It then needs no .NET
build.

Keep tests that touch WPF or WebView2 for a Windows job. For a WPF application,
keep the ViewModels and their tests in `net10.0` projects so they run on Linux.

## Package on Windows from the bundle

The `windows-package` job downloads the artifact and packages from it:

```sh
dotnet tool restore
dotnet runic package windows --project src/Acme.App --frontend-bundle web-bundle --output-directory artifacts/msix
```

The publish takes the page from the bundle. It runs no frontend install, no Vite
build and no client emission, so the job sets up neither Node.js nor Bun. Before
it uses the page, it checks the bundle against the build:

- the bundle must have an entry for `Acme.App` whose files hash as recorded,
  which catches a truncated download (`RAPPPKG1008`, `RUNICBRIDGE093`);
- every View must have the same fingerprint in the bundle as in this build
  (`RUNICBRIDGE094`);
- the page must have been built for this host (`RUNICBRIDGE095`).

A ViewModel that changed after the bundle was written stops the publish:

```
src/Acme.App/Acme.App.csproj : error RUNICBRIDGE094: The prebuilt frontend in web-bundle/ was built from other
ViewModels: Acme.App.CounterViewModel is B841442095549CE212F0198A1D4F9DF04546F1CBC4BA4C7B8EEC24208B170412 in this
build and C4212F571CFBD3580C1F987E71902FAE577D37C7DDBA2E243B381BD6FA230679 in the bundle. Build the bundle from the
same commit, or set RunicApplicationFrontendPrebuiltCheck=warning to publish anyway.
```

Write the bundle from the same commit, configuration and Runic release as the
package. The MSIX is unsigned. To sign it, pass `--certificate app.pfx` with the
password in `RUNIC_SIGNING_PASSWORD`, from repository or organization secrets.
The [packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/packaging.md)
lists the options.

`linux-package` packages the same bundle as a Debian package
(`dotnet runic package deb`). Another OS's package works the same way:
`dotnet runic package macos --frontend-bundle web-bundle` on a macOS runner.

## No Windows runner?

Without NativeAOT, a Linux runner can publish for Windows too, from the same
bundle:

```sh
dotnet publish src/Acme.App -c Release -r win-x64 --self-contained false \
  -p:RunicApplicationFrontendPrebuiltDirectory="$PWD/web-bundle" -o out-win
```

The result is a GUI executable, `Acme.App.exe`, with `www/` from the bundle.
NativeAOT, the MSIX and signing with `signtool` still need Windows.

## WPF projects on Linux

A project of a Windows target framework, such as a WPF application or model
project on `net10.0-windows`, restores and builds on Linux only with
`EnableWindowsTargeting=true`. Without it the restore stops with `NETSDK1100`.
[`Directory.Build.props`](Directory.Build.props) sets the property on every OS
but Windows. The application then builds, tests its `net10.0` ViewModels and
writes its bundle on Linux, but runs only on Windows. `dotnet runic doctor`
reports a Windows target framework without the property.

## GitHub, Gitea and Forgejo

| | GitHub Actions | Gitea Actions | Forgejo Actions |
| --- | --- | --- | --- |
| Workflow file | `.github/workflows/ci.yml` | `.github/workflows/ci.yml` (Gitea reads `.gitea/workflows` first, then `.github/workflows`) | `.forgejo/workflows/ci.yml`; Forgejo ignores `.github/workflows` while this folder exists |
| Artifact actions | `actions/upload-artifact`, `actions/download-artifact` | The same, with Gitea Runner 3.0 or later and Gitea 1.26.2 or later | `forgejo/upload-artifact@v4`, `forgejo/download-artifact@v4`: the stock v4 and later fail with `not currently supported on GHES` |
| Runner labels | Hosted `ubuntu-24.04`, `windows-2025` | The labels your runners registered; the official runner has Windows builds (host mode) | The labels your runners registered (`docker` by default); Windows runners are community builds |
| Signing secrets | `environment:` protection | `environment:` is ignored: keep the `if:` that limits packaging to `main` and tags | |

Set the repository or organization variables `LINUX_RUNNER` and
`WINDOWS_RUNNER` to your runners' labels; without them the workflow uses the
defaults in the table. With four Linux runners on Gitea, configure one shared
cache server for `actions/cache`, or each runner keeps its own.

Forgejo resolves `uses:` from its `DEFAULT_ACTIONS_URL`, `https://data.forgejo.org`
by default, which mirrors `actions/*` and `forgejo/*` at the same commits. An
instance with another value needs the same mirrors.

`ci/check-workflows.mjs` keeps the two files in step. Apart from the comment at
the top, they must have the same lines, except for the artifact actions and the
default runner labels. Every action must be pinned to a commit SHA with its
version in a comment:

```sh
node ci/check-workflows.mjs   # add --actionlint to require actionlint
```

## Caching

Cache the NuGet packages folder per OS, keyed on the project files, or on
`packages.lock.json` once you commit lock files. The runtime packs differ per
OS. Cache npm's download cache, keyed on `Frontend/package-lock.json`. Never
cache or upload `node_modules`: its Rolldown and Lightning CSS binaries are per
OS.

## Run it locally

```sh
ci/local-run.sh                 # add --package deb for the Debian package, --keep to keep the work folder
```

It runs both halves on one Linux machine, with .NET and Node.js installed, in
two fresh copies of the repository:

1. The Linux job: `dotnet test` and `dotnet runic bundle`.
2. The Windows job, with neither Node.js nor Bun on `PATH`: doctor's
   `web-bundle` check, then a framework-dependent `win-x64` publish from the
   bundle, whose `www/` must be the bundle's page plus the host's
   `runic-views.js`.
3. The negative case: a property added to `CounterViewModel` after the bundle
   was written. The publish must stop with `RUNICBRIDGE094`.

## Troubleshooting

| Message | Meaning |
| --- | --- |
| `RUNICBRIDGE092` | A project whose frontend build is off can't write its page to a bundle: bundle the application project, not a solution with the test project |
| `RAPPPKG1008`, `RUNICBRIDGE093` | The bundle is missing, has no entry for the project, or its files don't match the manifest, as after a truncated download |
| `RUNICBRIDGE094` | A View changed after the bundle was written. Write the bundle from the commit you package |
| `RUNICBRIDGE095` | The page was built for another host |
| `RUNICBRIDGE096` (warning) | An optimized publish takes a page not built with `NODE_ENV=production` |
| `NETSDK1100` | A Windows target framework on Linux without `EnableWindowsTargeting` |
| `Cross-OS native compilation is not supported.` | NativeAOT for another OS: package on that OS |
| `not currently supported on GHES` | The stock artifact actions on Forgejo: use `.forgejo/workflows/ci.yml` |

The diagnostics are described in
[docs/diagnostics.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md).

## Versions and lock files

The example names one Runic release: `RunicVersion` in `Directory.Packages.props`,
the `dotnet-runic` version in `.config/dotnet-tools.json` and
`@runic-artifex/views` in `Frontend/package.json`. Update the three together.
The third-party npm packages in `Frontend/package-lock.json` have their hashes.
The `@runic-artifex/views` entry doesn't, because runic-sdk tests the example
with unpublished candidates of that version; `npm ci` takes it from the
registry. In your copy, run `npm install` once in `src/Acme.App/Frontend` to
record its hash. Commit NuGet lock files too
(`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` and
`dotnet restore --locked-mode`), as the `runic-app` template does.

## Before 0.24

Runic 0.23 can already publish a page built elsewhere, with nothing to check it
against:

```sh
dotnet publish -c Release -r win-x64 -p:RunicApplicationFrontendBuildEnabled=false \
  -p:RunicApplicationFrontendOutputDirectory=<downloaded dist>
```

A page built from older ViewModels then publishes silently and fails only at
run time. 0.24's bundle and its fingerprint check close that gap.

## In runic-sdk

The package consumers job runs this example against the packed
packages on every package pull request (`package-smoke.mjs`). The engineering
job lints both workflow files and checks that they are in step.
