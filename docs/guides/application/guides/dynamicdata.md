# DynamicData collections

Use [Runic.DynamicData](https://github.com/Runic-Artifex/DynamicData) for the
Primitives flavor, or `Runic.DynamicData.Reactive` for the System.Reactive
flavor. Both target .NET 10; the namespaces remain `DynamicData` and
`DynamicData.Reactive`. Obtain the NuGet assets from the fork's GitHub releases
and add their directory as a local NuGet source. Select one flavor and remove
the equivalent upstream package. Current Runic uses ReactiveUI 26.0.1 and
Primitives 9.0.0; the fork also tests Primitives 8.4.0 used by ReactiveUI 25.

DynamicData's cache, filtering, grouping, transforms and disposal stay in .NET.
Runic exports the resulting collection through a generated, reflection-free
bridge. The [runnable example](../../../../examples/dynamicdata/README.md)
has a 100,000-row source cache, keyed browser rendering and an independent
viewport for each presentation.

## Bind on the model context

Inject `IRunicModelContext` and obtain its sequencer from
`RunicReactiveSchedulerProvider`. Bind the model with
`RunicModelContextRegistry.Shared.Bind(context, model)` and retain the lease
until disposal. Each window owns its model context; native UI dispatch remains
the host's responsibility.

```csharp
var changes = store.Connect()
    .SortAndVirtualize(comparer, requests)
    .ObserveOn(sequencer)
    .BatchBridgeSnapshots(this);

binding = changes
    .Bind(rows, new SortAndBindOptions { ResetThreshold = int.MaxValue })
    .Subscribe();
```

`BatchBridgeSnapshots` wraps each downstream changeset delivery. Place it after
`ObserveOn` and before `Bind` or `SortAndBind`; an outer batch around
`SourceCache.Edit` ends before deferred notifications reach the collection.
Dispose the binding, viewport signal, commands and model-context lease with
the presentation. The same extension is available from the Reactive flavor's
adapter namespace.

## Opt into collection updates

```csharp
public sealed record Row(int Id, string Label, int Value);

[RunicCollection(nameof(Row.Id))]
public ReadOnlyObservableCollection<Row> Rows { get; }
```

The property must be read-only and nonnullable, with nonnullable DTO rows.
Keys must be unique, stable and nonempty: supported key types are `int`,
`string` and `Guid`. Initialize the collection before attaching a bridge.
Collection notifications produce indexed add, remove, replace and move
operations. Immutable replacements and direct `INotifyPropertyChanged` row
notifications serialize only changed rows. Unchanged browser rows keep their
object identity, so render with the generated key rather than the array index.

Resets, mixed scalar/collection changes, nested row notifications and shared
DTO paths use an atomic full snapshot. Models exporting validation use full
snapshots to keep validation and state together. A changeset above 4,096
operations also falls back to a snapshot. Slow host delivery retains at most
64 pending frames or 1 MiB of encoded characters, then replaces pending work
with a recovery snapshot. Clients re-read a snapshot after a gap or malformed
delta; duplicates never apply twice.

## Bound the presented collection

Keep the large source cache shared and the viewport request and bound
collection per presentation. `SortAndVirtualize` suppresses updates outside
that viewport. The browser helper computes a fixed-height range:

```ts
const range = collectionViewport({
  totalCount, scrollTop, height, rowHeight: 32, overscan: 5,
});
await view.setViewport({ start: range.start, size: range.size });
```

Use `range.totalSize` for the scroll extent and position rendered rows at the
backend's reported start. Coalesce pending scroll requests to the most recent
range. This helper does not implement variable-height measurement or browser
rendering. The browser copies each changed field's array and checks its keys,
so a bounded viewport remains useful even when delta payloads are small.

## Performance evidence

The example's `--benchmark` compares the same DynamicData binding boundary
with full snapshots, batched snapshots, incremental updates and a viewport.
It reports synchronous application/encoding time, allocations and encoded
rows. It excludes native rendering, browser hydration and presentation time.
The direct collection boundary is common to Avalonia and MAUI; it is not a
measurement of those frameworks. See the example's assessment for results and
the remaining end-to-end comparison.
