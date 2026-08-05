# Wave B promotion evidence

## Decision

Decision: promote `RunicToolkit.Collections` to a G2 kernel/integration candidate.
The decision applies to the exact Wave B commit only after its final owned-path
handoff records all mandatory commands below passing from a clean worktree based on
main `374a1c5` or newer. This is a technical promotion from the Wave A contract
freeze, not a public release, reuse grant, or claim of external adoption.

Public source and package publication remain blocked by ADR 0004. The MVVM bridge
and framework-specific adapters remain deferred to Wave C and are outside the
Collections ownership boundary.

## Evidence map

| Claim | Executable evidence | Acceptance |
|---|---|---|
| Frozen range and reconciliation protocol | `tests/RunicToolkit.Collections.Tests` | All OR0–OR2 traces and Wave B model, boundary, property, reentrancy, exception, and adversarial-plan cases pass. |
| Determinism and identity retention | `PropertySequenceTests.cs`, `UpdateToTests.cs` | Repeated seeded traces match; FIFO/keyed identities and resolver behavior match the model. |
| Notification allocation/performance envelope | `benchmarks/RunicToolkit.Collections.Benchmarks -- --gate` | The complete matrix has expected event/identity counts and stays below `performance-gate-v1.md` allocation ceilings. |
| Reproducible benchmark baseline | `benchmarks/RunicToolkit.Collections.Benchmarks/baseline-v1.csv` and `--full` | CSV covers range policies and reconciliation strategies through size 10,000; handoff records the run without presenting timings as a universal guarantee. |
| Package structure and API consumption | `tests/RunicToolkit.Collections.PackageConsumer` | A temporary local-only feed/cache restores the packed package, validates nuspec/assets, builds, and runs the managed consumer. |
| Trim/Native-AOT safety | package consumer `--aot` and `tests/RunicToolkit.Collections.AotSmoke/run-native-smoke.ps1` | Current-host native executables publish with zero owned trim/AOT warnings, run, print PASS, and exit zero. |
| Architecture and identity | repository namespace/architecture/ownership gates | `RunicToolkit.Collections` is preserved and no dependency or edit crosses into MVVM or another task's paths. |

The package consumer is maintained by this repository. It proves package-boundary
compatibility but is not an independent external consumer. A current-host AOT run is
not by itself a cross-platform execution matrix; promotion evidence must name every
host/RID actually run and leave unexecuted platforms unclaimed.

## Mandatory handoff commands

Run restore and Release build for every owned project, then:

```console
dotnet run --project tests/RunicToolkit.Collections.Tests -c Release --no-build
dotnet run --project benchmarks/RunicToolkit.Collections.Benchmarks -c Release --no-build -- --gate
dotnet run --project benchmarks/RunicToolkit.Collections.Benchmarks -c Release --no-build -- --full
dotnet run --project tests/RunicToolkit.Collections.PackageConsumer -c Release --no-build
```

Run the packed consumer with `--aot` and the standalone native smoke on each host
available for the promotion matrix. Finally run the repository
namespace/architecture/ownership gates and require a clean worktree.

The handoff must record the commit SHA, test total, benchmark gate result and row
count, packed-consumer result, each native host/RID result, warnings, and any deferred
platform. Evidence belongs to that exact commit; results from a dirty tree or another
commit do not satisfy promotion.

## Deferred integration edges

- Wave C MVVM binding/dispatcher integration.
- Framework adapter behavior in Waves E and F.
- Cross-package compatibility with the later
  `RunicToolkit.Collections.Observable` identity, if that reserved package is used.
- Independent external-consumer evidence and ecosystem compatibility history.

Release-wide G4 items such as supported-OS coverage, SBOM/provenance, dependency
notices approval, deterministic artifact hashes across clean roots, and the license
decision remain outside this G2 promotion.
