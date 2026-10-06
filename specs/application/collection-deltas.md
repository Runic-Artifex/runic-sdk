# Collection delta frames

This is the contract for incremental collection updates in the
[Runic Application Views wire protocol](README.md), version 1. The producer is
the generated `ViewModelBridge` in `Runic.Application.Views`. The consumer is the
shared browser runtime in `@runic-artifex/views`, used by generated clients.
Both are tested against the [conformance fixtures](fixtures/collection-deltas).

Generated code is the only supported producer and consumer. Change the frame
format, the producer and the consumer together, and update the fixtures in the
same change.

## Opting in

A read-only ViewModel property of DTO rows marked `[RunicCollection(keyProperty)]`
publishes deltas, for example
`[RunicCollection(nameof(Row.Id))] ReadOnlyObservableCollection<Row> Rows`. The
generated client registers a codec and key function for the field with
`defineCollection` (see the [views package guide](../../packages/web/views/README.md#incremental-collections)).
The key property is a nonnullable `string`, `Guid` or `Int32`. Its wire key is the
string itself, the lowercase `D` form of a `Guid`, or the invariant decimal form
of an `Int32`. Keys must be nonempty and unique within the field; the client
rejects a state or frame that breaks this.

The opt-in changes the contract fingerprint. Generated clients and runtime
packages must come from the same build.

## Frames

A collection-only change publishes a frame on the route's state channel
(`__{route}Changed`) in place of a full state:

```json
{ "__runicDelta": 1, "baseRevision": 12, "revision": 16,
  "changes": [{ "field": "rows", "kind": "replace", "index": 2,
    "oldIndex": 2, "keys": ["42"], "items": [{ "id": 42, "label": "updated" }] }] }
```

| Member | Contract |
| --- | --- |
| `__runicDelta` | Frame format version. Always `1`. A full state never has this member. |
| `baseRevision` | The revision of the last state or frame the producer published on this route. |
| `revision` | The revision after the frame. Greater than `baseRevision`; not necessarily `baseRevision + 1`. |
| `changes` | 1 to 4,096 changes, applied in order. |

The producer writes the members in this order, without whitespace. A frame
never carries non-collection state; any other change publishes a full state.

### Changes

Every change has all six members, written in this order:

| Member | Contract |
| --- | --- |
| `field` | The generated wire name of a `[RunicCollection]` field. |
| `kind` | `add`, `remove`, `replace` or `move`. |
| `index` | See the table below. |
| `oldIndex` | See the table below. |
| `keys` | Nonempty array of row keys. |
| `items` | Array of rows encoded like the field's rows in a full state. Empty for `remove` and `move`. |

Changes apply sequentially: each index refers to the array as left by the
previous change in the same frame.

| Kind | `index` | `oldIndex` | `keys` | `items` | Effect |
| --- | --- | --- | --- | --- | --- |
| `add` | insertion point | `-1` | keys of the new rows | the new rows | Inserts `items` at `index`. |
| `remove` | first removed row | equal to `index` | keys of the removed rows | `[]` | Removes `keys.length` rows at `index`. |
| `replace` | first replaced row | equal to `index` | keys of the old rows | the new rows | Replaces `keys.length` rows at `index` with `items`. New rows may have other keys. |
| `move` | final position after removal | first moved row | keys of the moved rows | `[]` | Removes `keys.length` rows at `oldIndex`, then inserts them at `index`. |

Fixtures: [add](fixtures/collection-deltas/add.json),
[remove](fixtures/collection-deltas/remove.json),
[replace](fixtures/collection-deltas/replace.json),
[move](fixtures/collection-deltas/move.json) and an
[ordered batch](fixtures/collection-deltas/batch.json).

A property change on a row that implements `INotifyPropertyChanged` publishes a
`replace` of that row with `index` equal to `oldIndex`. Such rows need a stable
key.

## Producing frames

Each `INotifyCollectionChanged` notification and each row notification takes the
next revision (from the window's counter when the bridge belongs to a window
session, otherwise the bridge's own) and appends its changes. Without an open
`BridgeSnapshotBatch` the producer publishes at once, so a notification is one
frame. Inside a batch it publishes one frame, at the batch's final revision,
when the outermost batch ends.

The producer publishes a full state instead of a frame when any of the
following applies to the pending work:

- A `Reset` notification, or a notification without a starting index.
- A non-collection member changed (mixed changes publish one atomic state).
- Validation state, or a row reached through more than one DTO path (for example
  a collection row that is also exposed as `Selected`).
- More than 4,096 pending changes. The full state carries the final revision of
  the batch ([fixture](fixtures/collection-deltas/overflow.json)).
- Delivery cannot retain the frame (see [Delivery](#delivery-and-recovery-snapshots)).

`baseRevision` is the revision of the previous frame or full state that the
producer published on the route. After a full state, the next frame's
`baseRevision` is that state's revision.

## Delivery and recovery snapshots

Full states coalesce: a newer state replaces any frames and states not yet
delivered. Frames do not coalesce, because each depends on its predecessor. An
`IAsyncBridgeTransport` acknowledges each delivery before the next one starts.

A route retains at most 64 undelivered entries, or 1,048,576 characters
(UTF-16 code units of the encoded JSON), in its delivery queue. The entry being
delivered does not count; a queued full state counts like a frame. When a new
frame would exceed either bound, the producer publishes a full state instead.
That state supersedes everything queued and becomes the next frame's baseline
([fixture](fixtures/collection-deltas/recovery.json)).

Because a queued full state counts toward the character bound, a state of 1 MiB
or more is followed by further full states, each superseding the last, until the
host takes it. The fixture's "1 MiB of pending frames" case shows this.

## Applying frames

The client holds the current state and its revision for each route. For an
incoming frame it:

1. Recovers (below) if `__runicDelta` is not `1` or `revision` is not a safe
   integer.
2. Ignores the frame if `revision` is not greater than the current revision.
   Repeated and stale frames have no effect.
3. Recovers if it has no state yet, `baseRevision` is not the current revision,
   or `revision` is not greater than `baseRevision`.
4. Applies all changes to a copy of the state. It recovers, keeping the previous
   state, if `changes` is empty or longer than 4,096, a field is not a registered
   collection, an index is out of range, `keys` or `items` is not an array, the
   rows at the target position do not have the given `keys`, a row fails the
   field's codec, added rows do not have the given keys, a `replace` has
   `oldIndex` different from `index`, `remove`/`move` has items, or the result
   has duplicate or empty keys.
5. Commits the new state and revision, and notifies subscribers once. Rows that
   no change touched keep their object identity, as do non-collection members.
   Checked-field versions are unchanged.

A full state is accepted when its revision is at least the current revision,
whether it is pushed, returned by a route, or read by recovery.

### `recover()`

Recovery calls the route's `{route}Snapshot` once and accepts the returned
state. While that read is in flight, further frames that need recovery are
dropped rather than queued; frames that arrive after it completes are judged
against the recovered revision. A failed read is reported to the page's error
handler (`reportError`, or `console.error`) and is not retried: the client keeps
its last state until the next push or reply. Command replies and reconnects always carry full states.

## Conformance fixtures

Each file in [`fixtures/collection-deltas`](fixtures/collection-deltas) has a
`description` and `cases`. Every case uses the test ViewModel with a `rows`
collection of `{ id, label }` rows keyed by `id` and a `title` string (`"rows"`):

| Member | Meaning |
| --- | --- |
| `initial` | The route's snapshot state before the steps. Its rows seed the collection. |
| `steps` | `{ "batch": [mutation...] }` applies mutations in one `BridgeSnapshotBatch`; `{ "each": [mutation...] }` applies each collection notification on its own. |
| `delivery` | Optional. `"held"`: the host blocks the first delivery until all steps ran. |
| `frames` | The exact states and frames the host receives, in order. |
| `expected` | The route's snapshot state after the steps, and the client state after applying `frames`. |
| `dropped` | Optional. Indices of frames the client does not receive. |
| `recoveries` | Optional, default 0. The number of snapshot reads the client performs. Recovery reads answer `expected`. |

Mutations are `{ "op": "insert", "index", "item" }`, `{ "op": "removeAt", "index" }`,
`{ "op": "set", "index", "item" }`, `{ "op": "move", "oldIndex", "newIndex" }`
and `{ "op": "appendRows", "start", "count", "width"? }`, which appends generated
rows one notification at a time.

To keep large cases small, an array element that is a single-member object is a
generator:

- `{ "$rows": [start, count, width?] }` expands to `count` rows with ids from
  `start` and label `"row {id}"`, padded with `.` to `width` characters.
- `{ "$adds": [start, count, width?] }` expands to one `add` change per such row,
  at `index` equal to its id.

The .NET test (`CollectionDeltaConformanceTests` in
`tests/dotnet/Runic.Application.Testing.Tests`) drives the producer through the
steps and requires byte-identical frames and snapshot states after expanding the
generators and encoding them compactly. The views test
(`packages/web/views/test/collection-fixtures.test.ts`) serves `initial`, pushes
every frame not in `dropped` twice, and requires the client state to equal
`expected` after the given number of recoveries.
