# Compatibility and migration

## Supported surface

The Wave A public API baseline remains `PublicAPI.Shipped.txt` under the namespace
`WebUIToolkit.Collections`. The shipping target is `net10.0`. The implementation is
BCL-only, does not capture a dispatcher or synchronization context, and is not
thread-safe. Its owner must coordinate access and marshal changes to any UI thread
required by an observer.

Wave B makes no compatibility claim for another target framework, another public
namespace, or the separately reserved `WebUIToolkit.Collections.Observable` package
identity.

## Migrating from `ObservableCollection<T>`

1. Change the collection type to `ObservableRangeCollection<T>`. Existing
   single-item calls and subscriptions retain BCL event shapes.
2. Replace loops of `Add`, `Insert`, `RemoveAt`, `Move`, or indexer assignment with a
   range operation only when observers can accept a multi-item payload.
3. Use `RangeNotificationMode.Reset` for observers that cannot process multi-item
   Add, Remove, Replace, or Move. Single-item calls remain granular. An
   unequal-cardinality `ReplaceRange` always resets.
4. Treat the destination passed to `MoveRange` as the block's final index, not an
   insertion index calculated before removal.
5. Do not mutate the collection from source iterators, matching callbacks, or event
   handlers. This guard is intentionally stricter than the conditional reentrancy
   behavior of `ObservableCollection<T>`.

Item equality must remain side-effect-free. The inherited nonvirtual BCL
`Remove(T)`, `Contains(T)`, and `IndexOf(T)` methods search before reaching an
overridable collection hook, so a self-mutating `T.Equals` implementation cannot be
intercepted without changing the frozen public or inheritance surface.

## Migrating refresh code to `UpdateTo`

Use the comparer overload when duplicate values are meaningful; existing duplicates
are consumed FIFO. Prefer the keyed overload for large collections and stable unique
keys. Keyed matching is expected O(n); arbitrary comparer matching can be O(n²).

Omit `resolveMatch` to keep the existing object for each match. Supply it when a
matched item must be replaced or merged. The resolver is a planning callback: it
runs once per match before mutation and must not mutate the collection.

Choose notification policy explicitly at integration boundaries:

- `Granular` preserves edit-level notifications but subscriber failure can stop a
  multi-edit plan at a coherent intermediate state.
- `Reset` installs the final sequence before a single Reset notification.
- `Auto` is deterministic for a fixed source, target, and options; its strict
  threshold rules are defined in `NOTIFICATION-CONTRACT.md`.

Consumers should handle both Reset and granular actions even if current options
normally choose one strategy.

## Package-consumer scope

`tests/WebUIToolkit.Collections.PackageConsumer` is an isolated generated consumer
of the packed artifact. It checks package metadata/content, restores from a temporary
local-only feed and cache, compiles the frozen API, exercises managed behavior, and
can publish/run the same consumer with Native AOT. It is first-party compatibility
evidence, not evidence of an independent external consumer or ecosystem adoption.

The MVVM bridge and dispatcher ownership are Wave C integration edges. Framework-
specific collection adapters follow their owning UI integrations in Waves E and F.
They must consume this frozen contract without adding an MVVM dependency to the
Collections package.
