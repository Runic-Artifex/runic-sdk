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
of an `Int32`. Keys must be nonempty and unique within the field, and rows must
not be null.

### Invalid keys

The producer never writes a full state or frame with a null row or a null,
empty or duplicate key, and never throws into the code that changed the
collection or row: a collection is often changed by a binding (ReactiveUI,
DynamicData `Bind`) that an exception would end, and later change handlers
would be skipped ([fixture](fixtures/collection-deltas/keys.json)).

- **Tracking.** Each full state checks every row and records each row's key.
  A collection notification checks the rows it adds against those keys and
  removes rows by the key they were recorded with. A row property change whose
  key differs from its recorded key, as when a key changes in place, publishes
  a full state instead of a frame.
- **Withholding.** A change that adds an invalid key, a key changed in place,
  and a `Reset` or other unindexed notification require a full state. Capturing
  it checks every key. While the keys are invalid the route publishes no state
  or frame, and each later change tries the capture again. The first state
  captured with valid keys resumes publication, and frames follow it.
- **Reporting.** When a capture for delivery finds invalid keys, the producer
  logs event 1012 `BridgeCollectionKeysRejected` (model, field, key and route,
  with the exception) and publishes a failure notice on the state channel, once
  per distinct failure until a state is captured again. A synchronous recovery
  capture inside a change handler that fails, for any reason, becomes a
  request, which the delivery captures and reports.
- **Explicit reads.** A snapshot read, command reply or checked write reply
  that cannot write the state fails with `ok: false`, `state: null` and
  `error: { kind: "failed", message }`, and logs event 1012.

The failure notice is `{ "__runicFailure": 1, "revision", "error": { "kind",
"message", "detail"? } }`. `revision` is the producer's current revision and
does not change the client's. The message names the model, field and rows but
no key value, for example
`CollectionDeltaViewModel.rows: rows 0 and 1 have the same key. [RunicCollection] keys must be nonempty and unique within the collection.`
In development (see `BridgeDiagnostics`), `detail` carries the exception, whose
message names the key (`rows 0 and 1 have the duplicate key '1'`), as do the
log entries. The client reports the notice as a `failed` `BridgeError` for
`__{route}Changed` (`onBridgeDiagnostic` and `reportError`) and keeps its last
state.

The client also validates keys in full states as well as frames: a full state
with an empty or duplicate key fails the initial connection (the snapshot reply
is an invalid state) or, when pushed later, is rejected and reported, so the
client stays at its last state.

A ViewModel that implements `INotifyDataErrorInfo`, or whose state graph
contains a type that does, never publishes frames: the generated bridge marks
every collection descriptor `PublishesChanges: false` so validation stays atomic
with full states. The producer still checks keys in those full states, and the
generated client still registers `defineCollection` codecs, which then only
validate full states.

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
| `replace` | first replaced row | equal to `index` | keys of the old rows | the new rows, as many as `keys` | Replaces `keys.length` rows at `index` with `items`. New rows may have other keys. |
| `move` | final position after removal | first moved row | keys of the moved rows | `[]` | Removes `keys.length` rows at `oldIndex`, then inserts them at `index`. |

Fixtures: [add](fixtures/collection-deltas/add.json),
[remove](fixtures/collection-deltas/remove.json),
[replace](fixtures/collection-deltas/replace.json),
[move](fixtures/collection-deltas/move.json) and an
[ordered batch](fixtures/collection-deltas/batch.json).

The producer serialises each notification as raised. A custom
`INotifyCollectionChanged` whose `Replace` has different old and new item counts
produces a `replace` with `items.length` different from `keys.length`; the client
rejects that frame and recovers with a snapshot.

A property change on a row that implements `INotifyPropertyChanged` publishes a
`replace` of that row with `index` equal to `oldIndex`. Such rows need a stable
key. Only direct rows are matched: a property change on an object nested inside
a row publishes a full state.

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
- A row property change whose key differs from the key the row was recorded
  with, or a change that adds an invalid key (see [Invalid keys](#invalid-keys)).
- A row reached through more than one DTO path (for example a collection row
  that is also exposed as `Selected`), or a property change on an object nested
  inside a row.

(ViewModels with validation never publish frames at all; see
[Opting in](#opting-in).)
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

A full state is captured when the host takes it, not when it is requested, so
a burst of changes behind a busy host serializes the state once. The captured
state carries the revision current at that time. A frame produced while a
requested state waits is not queued: its change is part of that state, and the
next frame's `baseRevision` is the captured state's revision. A route reply
always captures its state at once.

A route retains at most 64 undelivered entries, or 1,048,576 characters
(UTF-16 code units of the encoded JSON), in its delivery queue. The entry being
delivered does not count. The limit counts characters, not bytes, so it is a
loose memory bound: .NET holds those characters in about 2 MiB, and their UTF-8
encoding can be up to 3 MiB. When a new frame would exceed either bound, the
producer publishes a recovery state instead. That state supersedes everything
queued and becomes the next frame's baseline
([fixture](fixtures/collection-deltas/recovery.json)). It is captured at once,
so later frames can queue behind it, and it counts toward the bounds like a
frame. When a later frame does not fit behind it, as after a recovery state of
1 MiB or more, the queued state reverts to a requested state, captured when
the host takes it, instead of serializing another full state per change. The
fixture's "1 MiB of pending frames" case shows this.

## Applying frames

The client holds the current state and its revision for each route. A message
with `__runicFailure` is a failure notice ([Invalid keys](#invalid-keys)): the
client reports it and changes nothing. For an incoming frame it:

1. Recovers (below) if `__runicDelta` is not `1` or `revision` is not a safe
   integer.
2. Ignores the frame if `revision` is not greater than the current revision.
   Repeated and stale frames have no effect.
3. Recovers if it has no state yet, `baseRevision` is not the current revision,
   or `revision` is not greater than `baseRevision`.
4. Applies all changes to a copy of the state. It recovers, keeping the previous
   state, if any change is invalid, including when: `changes` is not an array, is
   empty or is longer than 4,096; a change is not an object; `field` is not a
   registered collection; `kind` is unknown; `index` (or `oldIndex` for
   `replace` and `move`) is not a nonnegative safe integer or is out of range;
   `keys` or `items` is not an array; `keys` is empty or has an entry that is not
   a nonempty string; the rows at the target position do not have the given
   `keys`; a row fails the field's codec; `add` or `replace` has a different
   number of `items` than `keys`; added rows do not have the given keys; a
   `replace` has `oldIndex` different from `index`; `remove`/`move` has items;
   or the result has duplicate or empty keys.
5. Commits the new state and revision, and notifies subscribers once. Rows that
   no change touched keep their object identity, as do non-collection members.
   Checked-field versions are unchanged.

A full state, whether it is pushed, returned by a route, or read by recovery,
is accepted when its revision is greater than the current revision, or equal
to it and its content differs from the current state. The client keeps the
current wire state, with applied frames, for that comparison. A repeated full
state, or a command reply that matches the frames already applied, therefore
does not notify subscribers again. An equal revision with other content occurs
when a command changes state without a change notification; the reply then
carries the new content at the old revision.

### `recover()`

Recovery calls the route's `{route}Snapshot` and accepts the returned state.

- **Frames during the read.** While a recovery read is in flight, every frame
  that arrives is kept, in order, up to 64 frames (the producer's own pending
  bound). After the state is accepted the client applies them as if they
  arrived then: frames at or below the recovered revision are ignored, and a
  frame that does not continue the recovered state starts another recovery.
  If more than 64 frames arrived, the client recovers again when the newest
  discarded frame is above the recovered revision
  ([fixture](fixtures/collection-deltas/recovery.json), "frames that arrive
  during a snapshot read are applied after it").
- **Failed reads.** A failed read is retried, up to four reads in total, after
  250, 500 and 1,000 ms. Each failure that is retried is reported as a
  diagnostic (`onBridgeDiagnostic`); the last one is also reported to the
  page's error handler (`reportError`, or `console.error`). The client stops
  retrying, and reports the failure, as soon as the Bridge is disconnected (a
  reconnect re-reads every route), and stops silently once the route is
  disposed. After a recovery that failed, the client keeps its last state and
  discards the frames it kept; the next frame that needs recovery starts a new
  one ([fixture](fixtures/collection-deltas/recovery.json), "a failed snapshot
  read is retried").

Command replies and reconnects always carry full states.

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
| `recovery` | Optional, client only. The state recovery reads answer, default `expected`. An older state shows that the client keeps frames that arrive during the read. |
| `failedReads` | Optional, client only, default 0. The number of recovery reads that fail before one answers. |
| `recoveries` | Optional, default 0. The number of snapshot reads the client performs for recovery, including failed reads. |
| `error` | Optional. The failure message for invalid keys after the steps: a snapshot read fails with it (when `steps` is empty, the initial read already does). Such a case has no `expected`, and the client keeps `initial`. |

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
generators and encoding them compactly, or the exact `error`. The views test
(`packages/web/views/test/collection-fixtures.test.ts`) serves `initial`, pushes
every frame not in `dropped` twice, requires each frame up to the first dropped
one to notify subscribers exactly once, and requires the client state to equal
`expected` after the given number of recoveries. A failure notice must be
reported each time and change nothing. For an `error` case without steps it
requires the client to reject `initial`. Both tests run with failure detail
off, so failure notices are byte-identical.
