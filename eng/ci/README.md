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
bun run ci --job docs
bun run ci --job managed --matrix suite:application
bun run ci --job web --matrix package:application-bridge
bun run ci --job customers --matrix journey:dev
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
commands use Bun. Only jobs exercising npm/pnpm compatibility explicitly install
Node and those package managers. GitHub JavaScript actions have their own Node
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

Packages are materialized after the build independently of test completion.
Separate package-consumer, template and footprint jobs validate these candidates.
Only the final `verify` gate succeeding means all required jobs have passed.

Both desktop conformance paths (default and minimal host) use
`desktop.runsettings`: xUnit reports tests running longer than 30 seconds and
VSTest records diagnostics and TRX results. A two-minute hang limit aborts the
test host, with a three-minute session deadline and a five-minute Actions step
backstop for stalls outside individual tests. Failure artifacts are retained
by the following `always()` upload step, including for the minimal-host test
that runs before footprint publishing. No crash dumps are requested.

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
discovered from `eng/workspace.json`. Add new managed groups to the workflow matrix
and the discovery module together; contract tests check that assignment. Keep
focused package/test commands available for quick development without a container.

```sh
nix develop --command actionlint .github/workflows/ci.yml
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
