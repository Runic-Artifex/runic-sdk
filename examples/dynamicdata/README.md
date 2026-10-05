# DynamicData on current Runic SDK

This .NET 10 example keeps 100,000 immutable rows in a shared `SourceCache`
and presents a small, independently virtualized collection per window. The
generated client applies keyed collection changes and retains DOM rows across
updates. It uses the current SDK's Primitives adapter.

Clone the fork alongside the SDK, then run from the SDK root in its locked
development environment:

```sh
git clone https://github.com/Runic-Artifex/DynamicData ../DynamicData
bun install --frozen-lockfile
bun run --cwd packages/web/views build
dotnet run --project examples/dynamicdata -c Release \
  -p:DynamicDataForkRoot="$PWD/../DynamicData"
```

On NixOS use `direnv exec <sdk-root> <command>` when the SDK shell is not loaded.
The source dependency is explicit; it is not an SDK shipping dependency. To
copy the example outside this repository, replace the SDK source imports and
project references with the corresponding Runic packages, and the fork source
reference with `Runic.DynamicData` from its release assets.

Scroll the list and click **Update 100 rows**. Rows 0–99 change in the source;
an off-screen viewport receives no collection update. A second window can use
the same store with its own model and viewport signal. `--serve-only` serves
the real Desktop HTTP/WebSocket surface without opening a native window and
stops when stdin receives a line.

## Verification and benchmark

The headless consumer verifies independent viewports, off-screen suppression,
deferred changeset batching and generated collection deltas:

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

See the [DynamicData guide](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/application/guides/dynamicdata.md)
for model scheduling, supported row shapes and fallback behavior.
