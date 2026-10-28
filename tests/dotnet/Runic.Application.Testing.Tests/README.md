# Runic.Application.Testing.Tests

A TUnit test application on Microsoft.Testing.Platform. Run it from the
repository root in the project development environment:

```sh
direnv exec . dotnet run --project tests/dotnet/Runic.Application.Testing.Tests
```

Select a subset with a tree-node filter (`/<assembly>/<namespace>/<class>/<test>`):

```sh
# Presentation of navigation regions (the engine suite is tests/dotnet/Runic.Navigation.Tests)
direnv exec . dotnet run --project tests/dotnet/Runic.Application.Testing.Tests -- \
  --treenode-filter "/*/*/NavigationPresentationTests|ReactiveNavigationTests/*"
# Model context: ModelContextTests and ModelContextPresentationTests
direnv exec . dotnet run --project tests/dotnet/Runic.Application.Testing.Tests -- \
  --treenode-filter "/*/*/ModelContext*/*"
```

`ContinuationTests`, `AcceptedWorkScopeTests` and `RunicWindowHostTests` are
selected the same way, by class name. The cases run one at a time
(`[assembly: NotInParallel]` in `AssemblyInfo.cs`).

`PathWatcherTests` (category `FileSystem`) watch real temporary files,
symbolic links and Git repositories. The managed CI job runs them on Linux and
the native job on Windows and macOS. They skip, with the reason, when `git` is
missing or Windows does not allow creating symbolic links. The cases that
expect an event fail when a poll or the 30-second backstop read, rather than
an event, reported the change; `BackstopReadReportsChangesWithoutEvents` and
`PollingReportsChanges` cover those paths.

## Generated client checks

`GeneratedClientHarness` and `GeneratedInteractionClientHarness` are cases of
this suite. `GeneratedClientHarness` type-checks the generated clients, then runs
`GeneratedClientHarness.ts` live: `GeneratedClientLiveHost` serves the requests
of its generated clients from real windows of this process over Views protocol 4
(the script's standard input and output, `GeneratedLiveBridge.ts`), so the
generated decoders read what .NET writes and the encoders write what .NET
accepts. The reconnect and mock harnesses, and the interaction harness of
`GeneratedInteractionClientHarness`, run against the views mock bridge. `GeneratedHarnessProcess`
holds an exclusive checkout lock, bounds each child to 30 seconds and 512 MiB
observed resident memory, retains bounded output, and kills and reaps a failed
child before releasing the lock. Its supervisor runs outside Bun's event loop.
On Unix each child starts through `/bin/sh` with `ulimit -c 0` and then `exec`, so
an aborting child dies at once instead of staying alive while the kernel writes
its core dump, which can outlast the 30 seconds on a loaded runner.

For a direct Linux diagnostic run, use an external cgroup limit as well as a
wall-clock deadline (requires the systemd user manager):

```sh
direnv exec . bash -c 'systemd-run --user --unit=runic-generated-interaction-check \
  --wait --pipe --working-directory="$PWD" \
  -p MemoryMax=512M -p MemorySwapMax=0 -p RuntimeMaxSec=20s \
  -p TimeoutStopSec=2s -p KillMode=control-group \
  timeout --kill-after=2s 15s "$(command -v bun)" \
  tests/dotnet/Runic.Application.Testing.Tests/GeneratedInteractionClientHarness.ts \
  tests/dotnet/Runic.Application.Testing.Tests/obj/bridge-frontend/generated'
```

Use that single unit name; systemd rejects an overlapping launch. Wait for the
command's exit status before retrying. After a failure, inspect
`systemctl --user show runic-generated-interaction-check` for `MainPID`,
`ControlGroup`, `Result`, and `MemoryPeak`; stop the unit and confirm no remaining
processes before resetting its failed state. A tool's yielded command session is
still running: retain its session ID and poll or terminate it before a retry.
