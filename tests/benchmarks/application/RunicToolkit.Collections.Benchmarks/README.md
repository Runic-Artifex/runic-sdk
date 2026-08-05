# RunicToolkit.Collections benchmarks

This dependency-free executable records elapsed time, current-thread
allocations, observable event count, and retained object identities. Its CSV
output can be archived and compared without a benchmark framework.

Every mode starts with three deterministic warmup passes covering range and
reset range notifications plus Auto, Granular, and Reset reconciliation. It
then performs a full collection, so first-use JIT and static initialization are
kept out of the measured allocation windows as far as this lightweight harness
can reasonably arrange.

## Measurement modes

Quick validation covers sizes 10, 100, and 1,000 with three repetitions:

```console
dotnet restore benchmarks/RunicToolkit.Collections.Benchmarks
dotnet run -c Release --no-restore --project benchmarks/RunicToolkit.Collections.Benchmarks -- --quick
```

The full baseline adds size 10,000 and uses up to twelve repetitions, scaling
down for the quadratic duplicate-comparer path at larger sizes. Both modes
cover append, remove, replace, and move under Range and Reset policies, plus
keyed and duplicate-comparer reconciliation at 1%, 10%, and 50% churn under
Granular and Reset policies.

```console
dotnet run -c Release --no-restore --project benchmarks/RunicToolkit.Collections.Benchmarks -- --full
```

The versioned Wave B evidence is in [baseline-v1.csv](baseline-v1.csv). Elapsed
time is observational evidence only; it is deliberately not a release gate.

## Regression gate

Gate mode covers all 80 full-matrix rows with one repetition. It fails on an
event-count regression, loss of expected retained identities, excess measured
allocation, or an incomplete matrix:

```console
dotnet run -c Release --no-restore --project benchmarks/RunicToolkit.Collections.Benchmarks -- --gate
```

The thresholds and their rationale are frozen in
[performance-gate-v1.md](performance-gate-v1.md). Gate diagnostics go to
standard error, leaving standard output valid CSV. The exit code is `0` on
success, `1` for a gate violation, and `2` for invalid arguments.

Measurements include notification construction and synchronous observer
dispatch, but exclude collection/target construction and event subscription.
Allocations are current-thread values and therefore do not claim to measure
work an observer dispatches to another thread. These are workload and
regression signals, not a promise of universal speedup.
