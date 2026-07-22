# WebUIToolkit.Collections benchmarks

This dependency-free executable records elapsed time, approximate allocations on
the current thread, observable event count, and retained object identities. Output
is CSV so runs can be archived and compared without a benchmark framework.

Quick validation covers sizes 10, 100, and 1,000 with three repetitions:

```console
dotnet restore benchmarks/WebUIToolkit.Collections.Benchmarks --locked-mode
dotnet run -c Release --no-restore --project benchmarks/WebUIToolkit.Collections.Benchmarks -- --quick
```

The full baseline adds size 10,000 and uses up to twelve repetitions, scaling
down for the quadratic duplicate-comparer path at larger sizes. Both modes cover
append, remove, replace, and move under Range and Reset policies, plus keyed and
duplicate-comparer reconciliation at 1%, 10%, and 50% churn under Granular and
Reset policies.

```console
dotnet run -c Release --no-restore --project benchmarks/WebUIToolkit.Collections.Benchmarks -- --full
```

The numbers intentionally include notification construction and synchronous
observer dispatch. They are workload evidence, not a promise of universal speedup.
