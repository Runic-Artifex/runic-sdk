# Scaling suite

The scaling suite finds per-event, per-member and per-item costs that grow with the data, such as
[#887](https://github.com/Runic-Artifex/runic-sdk/issues/887), and fails CI when one gets worse. It gates on
deterministic counts, not on time: getter calls, `Observe` accessor calls, members and bytes serialized, rows scanned for
keys, row indexes rebuilt, pending changes visited, data graph walks, frame bytes and allocated bytes.

- Cases: [`tests/dotnet/Runic.Application.Scaling.Tests`](../dotnet/Runic.Application.Scaling.Tests), a TUnit suite
  over generated bridges and an `InMemoryBridgeTransport`.
- Budgets: [`budgets.json`](budgets.json), one entry per metric.
- Scripts: [`run.mjs`](run.mjs) (`bun run scaling`), [`report.mjs`](report.mjs) (the report and the job summary) and
  [`generate-fixtures.mjs`](generate-fixtures.mjs) (the sized ViewModels in `SizedFixtures.cs`).

## How a case is checked

A case measures its metrics at the two sizes of their budget entries (`sizes`, usually `n` and `4n`), twice at each
size, and fails when a count differs between the two runs. Each metric is then checked twice:

- **Class.** `O(1)`: the count at the larger size is at most the count at the smaller one. `O(n)`: at most `k` times
  it, where `k` is the ratio of the sizes. `O(n²)`: at most `k²` times it. `slack` adds a constant.
- **Limit.** The count at the larger size is at most `limit`.

A metric with a `margin` (allocated bytes) repeats within it, and its class and limit allow it. Its limit is the
measured value plus the margin.

A count below 75 % of its limit, or one that grows slower than its class, is an **improvement**. It does not fail
(D064.3); the report asks for the budget to be lowered.

## Budget entries

```json
"bridge/collection-burst/pending-scans": {
  "class": "O(n²)", "sizes": [256, 1024], "limit": 524800, "measured": [32896, 524800],
  "target": "O(n)", "known": "O(n²) per burst: …", "issue": "sdk#910"
}
```

- `class` and `sizes` are written by hand. A metric without an entry fails, and so does an entry without a result
  in a full run.
- `limit` and `measured` are written by `bun run scaling --update`.
- A **known cost** has `known`, the `issue` that tracks it and the `target` class. Its limit and class are today's,
  so it cannot get worse. The fix lowers them and removes `known`.
- `note` explains the expected value.

## Running and updating

```sh
bun run scaling                    # run every case, compare with budgets.json, print the report
bun run scaling -- --treenode-filter "/*/*/BridgeScalingTests/AddFrameBytesAsync"
bun run scaling --update           # record the results and rewrite measured and limit
```

`--update` never changes a class or the sizes. Review the diff of `budgets.json` before committing it. A pull request
that raises a limit says why under a "Budget changes" heading in its description.

## CI

The suite is part of the `application` managed group, so the `managed` job runs it on Linux whenever its component
(`scaling` in `eng/workspace.json`, which depends on `application`) is affected. Counts do not depend on the platform.
After the group, `report.mjs --require-all --base <base>` writes the job summary: every metric, the known costs, the
improvements and every budget that differs from the base branch's `budgets.json`. The results are kept as the
`scaling-results-<sha>` artifact for 30 days.

## Counters in the product

The Bridge counts its own work in an internal `BridgeCostCounters`
(`packages/dotnet/Runic.Application.Views/Generated/BridgeCostCounters.cs`), one per Bridge, read through the internal
`IBridgeCostSource`. Each count is a field add in code that holds the model gate already; nothing reads them outside
tests. When the Bridge runtime becomes non-generic (A1-b, #901), they stay an instance member of the runtime, with each
increment at the code it counts.
