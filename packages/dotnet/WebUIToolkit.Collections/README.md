# WebUIToolkit.Collections

`WebUIToolkit.Collections` is a BCL-only `net10.0` library for deterministic
observable range mutation and identity-preserving reconciliation.

`ObservableRangeCollection<T>` adds range insertion, removal, replacement, and
movement to the standard `ObservableCollection<T>` contract. `UpdateTo` supports
FIFO comparer matching or unique-key matching, preserves existing instances by
default, and selects granular or Reset notification plans through explicit policy.

The collection is deliberately single-owner and is not thread-safe. It captures no
dispatcher or synchronization context. Structural mutation attempted from input,
comparison, resolution, property, or collection callbacks throws
`InvalidOperationException`.

Source and package publication remain on hold under repository ADR 0004. No license
or permission to reuse or redistribute is granted by this build artifact.
