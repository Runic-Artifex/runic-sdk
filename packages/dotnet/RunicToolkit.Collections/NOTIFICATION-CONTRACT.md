# Observable range notification contract

This document records the frozen Wave A observable behavior of
`RunicToolkit.Collections.ObservableRangeCollection<T>`. Wave B hardens this
behavior; it does not change the namespace, public API, or event protocol.

## Common event rules

A successful non-empty operation applies its structural change before invoking
observers. Every observer therefore sees the post-operation collection. The event
order for one logical change is:

1. `PropertyChanged("Count")`, only when the item count changes.
2. `PropertyChanged("Item[]")`.
3. `CollectionChanged`.

An empty operation, an unchanged `MoveRange`, and an already-satisfied `UpdateTo`
emit nothing. A range call that affects exactly one item retains the ordinary
single-item `ObservableCollection<T>` event shape even when the collection was
created with `RangeNotificationMode.Reset`.

Multi-item Add, Remove, Replace, and Move events contain copied, read-only payloads.
The payload is isolated from later changes to the input sequence and collection.
Reset events have no old or new payload and use the standard `-1` indices.

## Range operation matrix

| Operation | Count changes | `Range` mode | `Reset` mode |
|---|---:|---|---|
| `AddRange` / `InsertRange`, one item | Yes | Add | Add |
| `AddRange` / `InsertRange`, multiple items | Yes | one range Add | Reset |
| `RemoveRange`, one item | Yes | Remove | Remove |
| `RemoveRange`, multiple items | Yes | one range Remove | Reset |
| `ReplaceRange`, equal non-zero counts of one | No | Replace | Replace |
| `ReplaceRange`, equal non-zero counts of many | No | one range Replace | Reset |
| `ReplaceRange`, unequal non-zero counts | Yes | Reset | Reset |
| `ReplaceRange`, zero old count | As for insertion | Add or no event | Add, Reset, or no event by inserted count |
| `ReplaceRange`, zero new count | As for removal | Remove or no event | Remove, Reset, or no event by removed count |
| `MoveRange`, one item | No | Move | Move |
| `MoveRange`, multiple items | No | one range Move | Reset |

`MoveRange(oldIndex, count, newIndex)` interprets `newIndex` as the block's index in
the final collection. Source and destination may overlap. A zero count or identical
old and final indices is a no-op.

Every input sequence is materialized exactly once before mutation, so a collection
may safely be its own range source. Argument validation, materialization, UpdateTo
matching, resolver execution, and edit planning finish before the first structural
change. Failure in those phases leaves state and notifications unchanged.

## Reconciliation

The comparer overload of `UpdateTo` matches duplicate values FIFO. The keyed
overload requires non-null, unique keys in both the existing and target sequences.
By default a match keeps the existing instance. When supplied, `resolveMatch` runs
exactly once per match in target order, and its return value becomes the desired
instance.

`UpdateNotificationMode.Granular` applies a deterministic sequence of ordinary
single-item Add, Remove, Move, and Replace notifications. Each edit follows the
common property/event order above. `UpdateNotificationMode.Reset` installs the
fully planned sequence and emits one Reset, preceded by `Count` when cardinality
changes and then `Item[]`.

`UpdateNotificationMode.Auto` uses Reset when either:

- the planned event count is greater than `MaxGranularEvents`; or
- `max(oldCount, newCount)` is at least `ResetRatioMinimumCount` and
  `eventCount / max(oldCount, newCount)` is greater than `ResetChangeRatio`.

Equality at either event-count or ratio threshold remains granular. A no-op remains
silent in every notification mode. `CollectionUpdateResult` reports the planned edit
counts; a Reset plan reports one notification, while a granular plan reports its
single-item event count.

## Reentrancy and exceptions

The collection has a single structural-mutation guard shared by inherited
single-item mutation hooks, range methods, and `UpdateTo`. Structural mutation from
input enumeration, `UpdateTo` comparers, key selectors, key hash/equality calls,
match resolution, or property/collection observers throws `InvalidOperationException`,
regardless of subscriber count. The guard is released after success or failure.

The inherited nonvirtual BCL `Remove(T)`, `Contains(T)`, and `IndexOf(T)` methods
perform their `T.Equals` search before any overridable mutation hook. Consequently,
a deliberately self-mutating `T.Equals` implementation is outside this guard. Fully
intercepting that search would require changing the frozen public surface or the BCL
base/backing-store contract; callers must keep item equality side-effect-free.

Observer exceptions are propagated without rollback. The collection has already
reached the state associated with the event being delivered, and invocation stops
at the throwing observer. For a range operation or Reset reconciliation this is the
final state. For granular reconciliation it may be a coherent partial edit-plan
state; the collection remains usable, but the caller must decide whether to retry or
perform a new reconciliation. No later event stage is synthesized after an earlier
stage throws.

The executable contract traces are in
`tests/RunicToolkit.Collections.Tests/RangeMutationTests.cs`,
`ReentrancyAndSafetyTests.cs`, and `UpdateToTests.cs`.
