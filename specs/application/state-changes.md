# Views protocol 4: state changes

This document belongs to the [Runic Application Views protocol](README.md), version 4. It specifies frames, the
changes they carry, how a producer produces them and a client applies them, and the delivery bounds and recovery. It
replaces [collection-deltas.md](collection-deltas.md), which describes protocol 3. The cases are in the
[`state-changes`](conformance/scenarios/state-changes) and [`replies`](conformance/scenarios/replies) scenarios.

## Frames

A frame changes the state of one route from the revision `baseRevision` to the revision `revision`:

```json
{"kind":"frame","route":"notes","baseRevision":41,"revision":42,"changes":[
  {"field":"rows","kind":"add","index":2,"oldIndex":-1,"keys":["7"],"items":[{"id":7,"label":"seven"}]},
  {"field":"count","kind":"set","value":3},
  {"field":"summary","kind":"set","value":"3 rows"}]}
```

| Member | Contract |
| --- | --- |
| `route` | The route. |
| `baseRevision` | The revision of the last state or frame the producer published on this route. |
| `revision` | The revision after the frame. Greater than `baseRevision`, not necessarily `baseRevision + 1`. |
| `changes` | 1 to 4,096 changes. |

Ordering rules:

- A frame has at most one `set` per field, and a field with a `set` has no other change in the same frame.
- The collection or text changes of one field apply in order: each index refers to the array as the previous change of
  that field left it.
- Changes of different fields are independent, so their order in `changes` carries no meaning.

A client rejects a frame that breaks these rules and recovers ([Applying frames](#applying-frames)).

## Changes

Every change has `field`, the wire name of a state member, and `kind`.

### `set`

`{"field","kind":"set","value","version"?}` replaces the member's whole value.

- `value` is encoded exactly as the member is in a full state.
- `version` is present exactly when the member is a checked field: its version after the change.
- A `set` may target any member, including a keyed collection, which replaces all of its rows (their keys are
  validated as in a full state), and a text field.

### Keyed collections

A **keyed collection** is a member whose value is an array of rows (objects), each with a **key**: a row field that
the contract names. The row field is a `string`, `uuid` or `int32`; its key is the string itself, the normal form of
the `uuid`, or the decimal form of the `int32`. Keys are nonempty and unique within the field, and rows are never
`null`.

The indexed changes have all six members:

| Member | Contract |
| --- | --- |
| `field` | The keyed collection. |
| `kind` | `add`, `remove`, `replace` or `move`. |
| `index` | See below. |
| `oldIndex` | See below. |
| `keys` | A nonempty array of row keys. |
| `items` | An array of rows, encoded as the field's rows are in a full state. Empty for `remove` and `move`. |

| Kind | `index` | `oldIndex` | `keys` | `items` | Effect |
| --- | --- | --- | --- | --- | --- |
| `add` | insertion point | `-1` | keys of the new rows | the new rows | Inserts `items` at `index`. |
| `remove` | first removed row | equal to `index` | keys of the removed rows | `[]` | Removes `keys.length` rows at `index`. |
| `replace` | first replaced row | equal to `index` | keys of the old rows | the new rows, as many as `keys` | Replaces `keys.length` rows at `index` with `items`. New rows may have other keys. |
| `move` | final position after removal | first moved row | keys of the moved rows | `[]` | Removes `keys.length` rows at `oldIndex`, then inserts them at `index`. |

### `text`

An **append-only text field** is a member whose value is a string (or `null`, when the contract declares it
nullable), such as a live log. Its changes are:

`{"field","kind":"text","length","trim","text"}`

| Member | Contract |
| --- | --- |
| `length` | The length, in UTF-16 code units, of the value the change applies to. |
| `trim` | The number of UTF-16 code units dropped from its start, from 0 to `length`. |
| `text` | The text appended after them. |

## Invalid keys

A producer never publishes a state or frame with a `null` row or a missing, empty or duplicate key, and never fails
the code that changed the collection: a collection is often changed by a binding that an error would end.

- **Withholding.** While a route's keys are invalid, the producer publishes no state or frame on it. Each later change
  tries again. The first publication with valid keys resumes with a frame on the last published revision, in which
  each keyed collection whose keys were invalid is a `set`, together with the other members that changed.
- **Reporting.** When a publication is withheld, the producer pushes
  `{"kind":"failure","route","revision","error":{"kind":"failed","message"}}`, once per field, problem and key until a
  publication succeeds again; rows that shift under the same invalid key do not report it again. `revision` is the
  producer's current revision and does not change the client's. The message names the field and the rows but no key
  value; in development, `detail` may name the key ([failure detail](README.md#failure-detail)). The client reports
  the notice as a `failed` error and keeps its last state.
- **Explicit reads.** An `open` or `snapshot` that cannot write the state fails with
  `{"kind":"failed","reason":"state-withheld"}`. A request that ran but whose effects cannot be published replies
  without `revision` and with the reason `state-withheld` ([Revisions in replies](README.md#revisions-in-replies)).
- **Client.** A client validates keys in full states as well as in frames. An `open` reply with an empty or duplicate
  key fails the open; a pushed state or a frame that produces one is rejected, reported, and the client stays at its
  last state.

## Producing frames

### Publication units

A producer groups the changes of a window into **publication units**. Each unit that changes a route publishes one
frame on that route (or a full state, below), and the unit's publication takes the next revision of the window's
counter. A unit never shows part of another unit's changes, and a frame is one unit, so a View whose validation spans
several members stays consistent.

How large a unit is, is the producer's choice: one change notification, a batch the application opens, everything a
window turn changed, or the notifications of a short interval, as a producer that keeps a minimum time between the units
of a route does. The conformance scenarios make each mutation step one unit; a producer that coalesces notifications by
time runs them without that interval.

### Members

At publication time, for each member other than keyed collections and text fields, the producer compares the member's
encoding with the encoding it last published on the route, by [canonical equality](values.md#canonical-equality).
Every member that differs becomes a `set`. A checked field whose version changed is a `set` with the new version, even
when its value is equal.

This comparison is deliberately not driven by change notifications: members that are computed from others and never
announce a change, availability members and validation results stay correct. Its cost is the encoding of the members
other than collections, once per publication.

### Keyed collections

The producer publishes the changes of a keyed collection as the operations that changed it, in order, one change per
notification: within one notification, inserting consecutive rows at one index is one `add`, removing consecutive rows
one `remove`, replacing consecutive rows one `replace` and moving consecutive rows one `move`; a change of a row's own
fields is a `replace` of that row with `index` equal to `oldIndex`. Changes from separate notifications stay separate
changes, also when they touch consecutive rows. Rows that change without the producer observing it are not published until the next `set` of the
field or full state, so rows must announce their changes, or be replaced.

It publishes a `set` of the field instead:

- when the collection was reset or changed in a way it cannot express as indexed changes, such as a change without a
  position, or a row's key changing in place;
- when the indexed changes of the unit would make the frame longer than 4,096 changes. The producer then replaces the
  indexed changes of keyed collections with one `set` each, starting with the field with the most changes, until the
  frame fits.

### Text fields

The producer compares a text field's value with the value it last published on the route. When they are equal,
nothing is published for the field. Otherwise it keeps the longest suffix of the old value that the new value starts
with: `trim` is the old length minus that suffix's length, and `text` is the rest of the new value. When nothing is
kept, `trim` is `length` and `text` is the whole value. It publishes a `set` instead when the old or the new value is
`null`, or when `text` would start inside a surrogate pair. Because the comparison happens at publication, several
appends in one unit are one change, and the result is the same in the producer and the client up to UTF-16 code
units.

### Full states

The producer publishes a full `state` push instead of a frame when the delivery queue overflowed (below), or whenever
it chooses to. A full state's revision is the revision of the unit it was captured in. After a full state, the next
frame's `baseRevision` is that state's revision.

## Delivery and recovery

### Delivery queue

The producer delivers the pushes of each route in order. Frames do not coalesce, because each depends on its
predecessor. Full states coalesce: a newer state replaces the frames and states of the route not yet delivered.

- A route retains at most **64 undelivered entries or 1,048,576 bytes** (the UTF-8 length of the encoded pushes) in its
  delivery queue. The entry being delivered does not count.
- When a new frame would exceed either bound, the producer captures a **recovery state** at once. It supersedes
  everything queued and becomes the next frame's baseline. Later frames queue behind it, and it counts toward the
  bounds like a frame.
- When a later frame does not fit behind the recovery state, as after a recovery state of 1 MiB or more, the queued
  state becomes a **requested state**: it is captured when the transport takes it, with the revision current then, so
  a burst of changes behind a busy transport serializes the state once. A frame produced while a requested state waits
  is not queued; its change is part of that state.
- A reply's state (`open`, `snapshot`) is always captured at once.

### Applying frames

The client holds the current state and revision of each open route. For an incoming frame it:

1. Recovers (below) if `baseRevision` or `revision` is not a safe integer.
2. Ignores the frame if `revision` is not greater than the current revision. Repeated and stale frames have no effect.
3. Recovers if it has no state yet, `baseRevision` is not the current revision, or `revision` is not greater than
   `baseRevision`.
4. Applies all changes to a copy of the state. It recovers, keeping the previous state, if any change is invalid,
   including when:
   - `changes` is not an array, is empty or is longer than 4,096, or a change is not an object;
   - `kind` is not a kind the client knows;
   - `field` is not a member of the route, or not a keyed collection for an indexed change, or not a text field for a
     `text` change;
   - the ordering rules are broken (two `set` changes of one field, or a `set` and another change of one field);
   - a `set` value fails the member's decoder, or `version` is missing for a checked field, present for another
     member, or not a safe integer;
   - for an indexed change: `index` (and `oldIndex` for `replace` and `move`) is not a non-negative safe integer or is
     out of range; `oldIndex` of an `add` is not `-1`; `keys` or `items` is not an array; `keys` is empty or has an
     entry that is not a nonempty string; the rows at the target position do not have the given keys; a row fails the
     field's decoder; an `add` or `replace` has a different number of `items` than `keys`; added rows do not have the
     given keys; a `replace` has `oldIndex` different from `index`; a `remove` or `move` has items; or the result has
     duplicate or empty keys;
   - for a `text` change: the current value is not a string or its length differs from `length`; `trim` is not a
     non-negative safe integer up to `length`; or `text` is not a string.
5. Commits the new state and revision and notifies subscribers once. Members and rows that no change touched keep
   their identity. A `set` with `version` updates the checked field's version.

A pushed full state is accepted when its revision is greater than the current revision. A full state's text field
must be a string, or `null` when it is nullable.

### Recovery

To recover, the client reads the route with `snapshot` and accepts the state it returns.

- **Frames during the read.** While a recovery read is in flight, the client keeps every frame that arrives, in
  order, up to 64 frames. After the state is accepted it applies them as if they arrived then: frames at or below the
  recovered revision are ignored, and a frame that does not continue the recovered state starts another recovery. If
  more than 64 frames arrived, the client recovers again when the newest discarded frame is above the recovered
  revision.
- **Failed reads.** A failed read is retried, up to four reads in total, after 250, 500 and 1,000 ms. Each failure that
  is retried is reported as a diagnostic; the last one is also reported as an error. The client stops retrying, and
  reports the failure, as soon as the transport disconnects (the new connection opens every route again), and stops
  silently once the route is closed or the producer answers `released`. After a recovery that failed, the client
  keeps its last state and discards the frames it kept; the next frame that needs recovery starts a new one.
