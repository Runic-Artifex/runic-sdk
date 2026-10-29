# DynamicData on current Runic SDK

This .NET 10 example keeps 100,000 immutable rows in a shared `SourceCache`
and presents a small, independently virtualized collection per window. The
generated client applies keyed collection changes and retains DOM rows across
updates. It uses the current SDK's Primitives adapter.

Clone the fork alongside the SDK at the pinned revision, then run from the SDK
root in its locked development environment:

```sh
git clone https://github.com/Runic-Artifex/DynamicData ../DynamicData
git -C ../DynamicData checkout --detach "$(bun -p 'require("./eng/workspace.json").dynamicData.forkRevision')"
bun install --frozen-lockfile
bun run --cwd packages/web/views build
dotnet run --project examples/dynamicdata -c Release \
  -p:DynamicDataForkRoot="$PWD/../DynamicData"
```

On NixOS use `direnv exec <sdk-root> <command>` when the SDK shell is not loaded.
The build fails when the checkout is not at the pinned revision; pass
`-p:RunicCheckDynamicDataForkRevision=false` to try another revision.
The source dependency is explicit; it is not an SDK shipping dependency. To
copy the example outside this repository, replace the SDK source imports and
project references with the corresponding Runic packages, and the fork source
reference with the [`RunicArtifex.DynamicData`](https://www.nuget.org/packages/RunicArtifex.DynamicData)
package from nuget.org (`RunicArtifex.DynamicData.Reactive` for the System.Reactive
flavor). The Primitives flavor keeps upstream's namespaces (`DynamicData`,
`DynamicData.Binding`, …); the System.Reactive flavor's are `DynamicData.Reactive`
and its children (`DynamicData.Reactive.Binding`, …), so a file imports the ones of
its flavor. Use the System.Reactive flavor with `ReactiveUI.Reactive` and
`Runic.Application.Views.ReactiveUI.Reactive`;
[`tests/fixtures/application/dynamicdata-reactive`](../../tests/fixtures/application/dynamicdata-reactive)
is the same viewport on that flavor.

Scroll the list and click **Update 100 rows**. Rows 0–99 change in the source;
an off-screen viewport receives no collection update. A second window can use
the same store with its own model and viewport signal. `--serve-only` serves
the real Desktop HTTP/WebSocket surface without opening a native window and
stops when stdin receives a line.

## Verification and benchmark

The headless consumer verifies independent viewports, off-screen suppression,
deferred changeset batching, generated collection deltas, withheld invalid keys
and that the frames of a viewport move and of `SortAndBind` comparer changes and
moves, applied as a client applies them, give the next snapshot:

```sh
dotnet run --project tests/fixtures/application/dynamicdata -c Release \
  -p:DynamicDataForkRoot="$PWD/../DynamicData"
dotnet publish tests/fixtures/application/dynamicdata -c Release -r linux-x64 \
  --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true \
  -p:DynamicDataForkRoot="$PWD/../DynamicData" -o artifacts/dynamicdata-aot
artifacts/dynamicdata-aot/DynamicDataProof
DOTNET_TieredCompilation=0 dotnet run \
  --project tests/fixtures/application/dynamicdata -c Release --no-build \
  -p:DynamicDataForkRoot="$PWD/../DynamicData" -- --benchmark
```

The benchmark uses 10,000 source rows, 1/10/100 edits per batch, 50 warmups
and 20 measured samples. The viewport contains 100 rows. Disable tiered
compilation for comparable scenarios; run it without other CPU-heavy checks.
Serialization counts include captured rows even when delivery coalesces a
snapshot. Timing excludes asynchronous transport, browser hydration and native
rendering. See [assessment](assessment.md) for retained results and limits.

## Fork revision

`dynamicData.forkRevision` in [`eng/workspace.json`](../../eng/workspace.json)
is the only record of the reviewed fork commit. The
[DynamicData consumer workflow](../../.github/workflows/dynamicdata.yml) checks
out that revision, checks that it is the fork's tag
`v{dynamicData.packageVersion}`, and runs the consumer, the System.Reactive
consumer, this example and the NativeAOT build. It runs when this example, the
fixtures, the Application or Navigation packages, the central build and package
files, the SDK version, the setup action or the pin change. It is not a required check: a path-filtered workflow never reports on
pull requests it skips.

The fork reviews upstream DynamicData monthly
([procedure](https://github.com/Runic-Artifex/DynamicData/blob/main/docs/maintenance.md#monthly-upstream-review))
and publishes tested commits as `v10.0.0-runic.N` releases, pushed to nuget.org as
`RunicArtifex.DynamicData` and `RunicArtifex.DynamicData.Reactive` `10.0.0-runic.N`.
Its [fork differences](https://github.com/Runic-Artifex/DynamicData/blob/main/docs/fork-differences.md)
list the behaviour changes to review. To adopt a new revision:

1. Choose a released fork commit; prefer the release that follows a completed
   monthly review.
2. Update `forkRevision` and `packageVersion` in a pull request, and the
   `RunicArtifex.DynamicData` versions that lock files resolve (through
   `RunicArtifex.ReactiveUI.Validation`) to the same release; the engineering
   tests check the locks, and the pin change runs the workflow.
3. Check out that revision and run the headless consumer and the benchmark
   above. If the benchmark changes noticeably, update [assessment](assessment.md).
4. Merge only when the workflow passes, and note the fork release in the pull
   request.

Because the workflow is not required, check its latest `main` run before a
release.

See the [DynamicData guide](https://docs.runic-artifex.eu/guides/application/guides/dynamicdata/)
for model scheduling, supported row shapes and fallback behavior.
