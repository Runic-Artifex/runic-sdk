# Collections performance gate v1

This file versions the deterministic acceptance rules used by `--gate`.
Wall-clock time is recorded in the CSV but never causes a failure because host
load, power policy, runtime tiering, and virtualization make small standalone
timing thresholds brittle.

`baseline-v1.csv` was generated on 2026-07-22 from the Wave B working tree based
on main `374a1c5`, using .NET SDK 10.0.302 on Windows 10.0.26200 x64 and an AMD64
Family 26 Model 36 processor. The handoff SHA identifies the promoted source;
the recorded timings remain host-specific, non-gating observations.

## Matrix

- Sizes: 10, 100, 1,000, and 10,000.
- Range operations: append, remove, replace, and move.
- Range policies: Range and Reset.
- Reconciliation: unique-key and duplicate-comparer/FIFO matching.
- Reconciliation churn: 1%, 10%, and 50%, rounded down with a minimum of one.
- Reconciliation policies: Granular and Reset.
- Repetitions: one, producing exactly 80 rows.

## Correctness thresholds

Every non-empty range operation emits exactly one collection event. Reset
reconciliation emits exactly one collection event. The deliberately shaped
Granular reconciliation workload replaces each churned tail item and therefore
emits exactly `2 * churnCount` events: one removal and one insertion per item.

Append and move retain all initial identities. Remove and replace retain
`size - max(1, size / 10)`. Both reconciliation paths retain
`size - churnCount`. The harness also verifies logical convergence before a row
is recorded.

## Allocation ceilings

Ceilings are per operation and scale with input size. They are intentionally
several times the Wave B baseline so normal runtime/platform variation does not
produce noise, while a new per-item notification cascade or accidentally
quadratic allocation remains visible.

| Scenario | Maximum measured bytes per operation |
| --- | ---: |
| Range append | `65,536 + 256 * size` |
| Range remove or move | `65,536 + 64 * size` |
| Range replace | `65,536 + 128 * size` |
| Keyed or duplicate reconciliation | `262,144 + 1,024 * size` |

The allocation window includes the mutation/reconciliation and synchronous
notification delivery. It excludes fixture construction, target construction,
event subscription, convergence checking, and identity counting. Changing the
matrix or any threshold requires a new versioned gate and evidence file rather
than silently rewriting v1.
