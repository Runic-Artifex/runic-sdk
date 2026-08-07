# RunicToolkit.Collections

`RunicToolkit.Collections` is a BCL-only `net10.0` library for deterministic
observable range mutation and identity-preserving reconciliation.

`ObservableRangeCollection<T>` adds range insertion, removal, replacement, and
movement to the standard `ObservableCollection<T>` contract. `UpdateTo` supports
FIFO comparer matching or unique-key matching, preserves existing instances by
default, and selects granular or Reset notification plans through explicit policy.

For every non-empty range mutation, observers see the completed collection before
notifications begin. Notifications are ordered as `Count` (only when cardinality
changes), `Item[]`, then `CollectionChanged`. Range payloads are copied and
read-only. Empty operations are silent, single-item operations retain ordinary BCL
event shapes, and unequal-cardinality replacement reports Reset.

The collection is deliberately single-owner and is not thread-safe. It captures no
dispatcher or synchronization context. Structural mutation attempted from input,
`UpdateTo` matching, resolution, property, or collection callbacks throws
`InvalidOperationException`.

The frozen behavior, compatibility guidance, dependency inventory, and Wave B
evidence are maintained in `NOTIFICATION-CONTRACT.md`, `COMPATIBILITY.md`,
`DEPENDENCIES.md`, and `PROMOTION-EVIDENCE.md` in the source repository. Application
Bridge and framework-specific collection adapters remain outside this package.
This library has no bridge dependency or dispatcher integration.

Source and package publication remain on hold under repository ADR 0004. No license
or permission to reuse or redistribute is granted by this build artifact.
