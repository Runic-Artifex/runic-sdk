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

## Generated client checks

`GeneratedClientHarness` and `GeneratedInteractionClientHarness` are cases of
this suite. The first starts this executable as a helper with
`--export-generated-client-fixture <path>` (handled by the hand-written
`Program.cs` before the test platform starts) to export actual C# wire payloads,
then runs the generated data/command client harnesses; the second runs the
interaction lifecycle harness. `GeneratedHarnessProcess`
holds an exclusive checkout lock, bounds each child to 30 seconds and 512 MiB
observed resident memory, retains bounded output, and kills and reaps a failed
child before releasing the lock. Its supervisor runs outside Bun's event loop.

The interaction mock uses finite queues: each scenario explicitly supplies its
prompts, then holds the next poll. Each scenario drains its polls and handlers
before the next starts. Diagnostics retain 16 route names, with a 64-call failure
limit. Do not replace an empty queue with an immediately resolved prompt.

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
