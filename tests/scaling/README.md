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
- NativeAOT size: [`aot-size/`](aot-size), the `aot/` entries of `budgets.json` (see [NativeAOT size](#nativeaot-size)).

## How a case is checked

A case measures its metrics at the two sizes of their budget entries (`sizes`, usually `n` and `4n`), twice at each
size, and fails when a count differs between the two runs. Each metric is then checked twice:

- **Class.** `O(1)`: the count at the larger size is at most the count at the smaller one. `O(n)`: at most `k` times
  it, where `k` is the ratio of the sizes. `O(n²)`: at most `k²` times it. `slack` adds a constant; `noise` (NativeAOT bytes) is how far each
  measurement may sit off the line, added to the smaller one before the factor and to the larger one.
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

## NativeAOT size

Each bridged ViewModel adds NativeAOT executable size. Until 0.19 the Bridge runtime was generic over the ViewModel, and
each ViewModel brought its own copy of the runtime's type descriptors, about 118 KiB
([#901](https://github.com/Runic-Artifex/runic-sdk/issues/901)). The runtime now holds the ViewModel as `object`, so a
ViewModel adds only its generated Bridge and codecs. The `aot/` budgets gate it on linux-x64 (D064.5, D064.24):

```sh
bun tests/scaling/aot-size/measure.mjs            # publish, measure, compare with budgets.json, print the report
bun tests/scaling/aot-size/measure.mjs --update   # record the results and rewrite the aot/ entries' measured and limit
                                                  # (not a limit marked "fixedLimit": a decision, kept as it is)
bun tests/scaling/aot-size/measure.mjs --keep     # also keep the executables, mstat files and records.tsv
```

- [`aot-size/fixture.mjs`](aot-size/fixture.mjs) writes K CommunityToolkit ViewModels of one shape, with each member
  kind of the Bridge's descriptor table (D072.15): six settable members (checked properties: a number, a text, a flag,
  a fraction, an enum and a record with field paths), a read-only number and text member, a keyed collection, an
  observed sub-object, a view request, a view question and three commands (synchronous, asynchronous, and asynchronous
  with an input and a result). A shell of the same shape is the one window the application opens, so the Bridge runtime
  and the host window cost the same at every K.
- `measure.mjs` publishes [`aot-size/Fixture`](aot-size/Fixture) with `PublishAot=true` and
  `-p:IlcGenerateMstatFile=true` for linux-x64 with K = 0 and at each size of the entries (1 and 9), about 20 s each
  with warm packages, then the Desktop and Translations fixtures (below) once each. [`aot-size/MstatReader`](aot-size/MstatReader) prints ILC's mstat records
  (`kind<TAB>size<TAB>owner<TAB>name`).
- `aot/bridge/executable-bytes` (O(n)): the executable bytes above the build with K = 0. Section alignment makes it
  not exactly linear: a 4 KiB section boundary can move either measurement by a few KiB, which the factor of 9
  amplifies at K = 1. So it has a `noise` of 8 KiB and a 2 % `margin`; its `limit` is the gate on the size itself.
- `aot/bridge/view-model-types` (O(n)): the mstat type records that name a fixture ViewModel.
- `aot/bridge/view-model-methods` (O(n), D072.15): the mstat method records of a fixture ViewModel's generated Bridge
  (`Generated.Bridges.Vm<N>Bridge` and its nested types). It has no section-alignment noise and catches lambda and
  state-machine fan-out at once. Its limit is the measured 34 per ViewModel at K = 9 (D072.15), fixed (`fixedLimit`) and
  without margin, since method counts are exact: a generator change that adds methods per ViewModel must justify
  raising it.
- `aot/bridge/generated-generic-types` (O(n)): #901's metric, the type records that instantiate a
  `Runic.Application.Views.Generated` type over a fixture ViewModel, also inside another generic.
- `aot/executable-bytes` (class `total`, no growth check): the whole executable at the larger size, with a 2 % margin,
  so that a fixed-size regression is also caught.
- `aot/framework/System.Net.Http` and `aot/framework/System.Net.Security` (`total`, limit 0): the mstat bytes those
  assemblies own. Neither may be in a CS-WebUI application on linux-x64 (D072.2): a failure means something reached
  the HTTP client or TLS stack again, as `HttpListener` did before 0.22.
- `aot/assembly/<Runic assembly>` (`total`, 5 % margin) for Runic.Application.Views, Runic.Navigation,
  Runic.Application.Views.CsWebUi and Runic.Assets: the mstat bytes each assembly owns, so a regression names its
  package. mstat bytes do not move with section alignment, so they need no noise.

An assembly owns the methods, type nodes, frozen objects and RVA data that MstatReader attributes to it: the declaring
type's assembly, for a generic instantiation its definition's (a `List<T>` over a Runic type is
System.Private.CoreLib's), and for a frozen object the assembly of the type whose static holds it. Blobs (metadata,
stack-trace data, hashtables) and string literals belong to no Runic assembly.

Two more fixtures share the ViewModels, each published once with K = 0 (sizes `[0, 0]`):

- [`aot-size/DesktopFixture`](aot-size/DesktopFixture): the same application on the Runic.Desktop host with
  `RunicDesktopServer=None` and no server package, so the host is measured without a presentation server.
  `aot/desktop/executable-bytes` (2 % margin), and `aot/desktop/framework/System.Net.Http` and
  `…/System.Net.Security` with limit 0.
- [`aot-size/TranslationsFixture`](aot-size/TranslationsFixture): the CS-WebUI fixture with Runic.Translations.Hosting
  and Runic.Application.Views.Translations registered. `aot/views-translations/executable-bytes` (2 % margin).

`--rid` with another runtime publishes only the CS-WebUI fixture and lists every metric's values without checking them.

The report adds a table per ViewModel: each measurement at K = 0, 1 and 9 and its slope from 1 to 9. The counts are
deterministic for a given ILCompiler; a .NET SDK update can change them and then needs `--update`.
`aot/bridge/generated-generic-types` must stay 0: a generic type of the generated-code support API closed over a
ViewModel brings the per-ViewModel copies back.

In CI the managed job's application group runs `measure.mjs` after the scaling report, and the results are part of
the `scaling-results-<sha>` artifact.
`--rid win-x64` or `--rid osx-arm64` measures on that runtime and only reports (D064.25). A pull request labelled
`ci:all-platforms` runs it on the Windows x64 and macOS arm64 native legs, with `--keep`, and keeps the mstat files in
the `aot-size-<rid>-<run>` artifact.

## Counters in the product

The Bridge counts its own work in an internal `BridgeCostCounters`
(`packages/dotnet/Runic.Application.Views/Generated/BridgeCostCounters.cs`), one per Bridge, read through the internal
`IBridgeCostSource`. Each count is a field add in code that holds the model gate already; nothing reads them outside
tests. They are an instance member of the non-generic runtime, with each increment at the code it counts.
