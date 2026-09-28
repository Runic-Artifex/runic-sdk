# Runic Application

This is the host-neutral replacement for the preview Application Bridge. It
selects contracts through explicit partial `RunicWindow<TViewModel>` and
`RunicView<TViewModel>` classes, then generates C# attachments and ordinary
TypeScript modules after the app's MVVM source generators run. There is no
`[RunicViewModel]` fallback or adapter to the old bridge in this package.

A View is a logical .NET presentation object with typed `DataContext`. Its
browser counterpart can be a plain TypeScript function or a component in a web
framework. The browser sends mount and unmount acknowledgements so a content
session creates a fresh View for each presentation and releases its lifetime
when the outlet changes. The ViewModel belongs to its application or window DI
scope and may survive View changes. Window-local routes and operation admission
are host-neutral; native window creation belongs to a host adapter.

Generated clients expose a snapshot, subscriptions, typed property setters,
commands, and disposal. Calls reject with typed errors. Accepted operations
remain owned by .NET across a browser reload; a reconnect gets a new snapshot.
A transport failure after acceptance can leave completion unknown, so callers
should inspect state before retrying a non-idempotent command. The generated
field-write API returns receipts and version conflicts; frontend form helpers
must await pending edits before issuing Save.

The runtime emits state and replies with generated and explicit
`Utf8JsonWriter` code. The build tool inspects the compiled application;
release runtime serialization does not reflect over ViewModel members. The
first-window package is exercised through Native AOT and Chromium in CI.
`RUNICBRIDGE002` rejects a
selected CommunityToolkit `ObservableValidator` ViewModel in Native AOT until
its validation behavior is verified.

The [first-window](../../../examples/first-window/README.md),
[Toolkit Notes](../../../examples/notes-view-first/README.md), and
[Reactive Notes](../../../examples/notes-reactive-views/README.md) examples
exercise the packaged graph and generated ordinary TypeScript client.

## Typed commands and checked writes

CommunityToolkit `IRelayCommand<T>` and `IAsyncRelayCommand<T>` use the same
supported input shapes and generated codecs as ReactiveUI commands: scalars,
nullable values, DTOs, collections, unions, and explicit custom codecs. Generated
`canX(input)` queries use the actual parameter. Async Toolkit commands expose
`startX`, completion, recovery, and cancellation, with no invented result value.
Toolkit cancellation calls the command's `Cancel()` and therefore targets its
current execution; it does not isolate concurrent invocations of the same
command instance.

Checked-write receipts decode their values just like state: for example, an
`Int64` receipt's `snapshot.value` or conflict's `incoming.value` is a `bigint`.
An identical request ID and payload replays its receipt. Reusing an ID with a
different baseline version, baseline value, or new value produces a conflict.
Each field retains at most 64 receipts within a default 256 KiB encoded-value
and receipt-metadata budget. Evicted or oversized receipts leave bounded
request-ID tombstones; reconcile authoritative state before issuing a new ID.
The write may have succeeded even when its receipt could not be retained.

## Batch synchronous model updates

```csharp
using (BridgeSnapshotBatch.Begin(document))
{
    document.Title = imported.Title;
    document.Content = imported.Content;
    document.Language = imported.Language;
}
```

Nested scopes on the same model capture and publish one final snapshot per
attached bridge when the outer scope ends. Notifications still advance the
revision, and direct command/checked-write replies capture current state
immediately. A batch controls snapshot work; it does not make mutations atomic
or own the model scheduler. Enter the model's execution context as usual and
keep the batch around synchronous changes, after awaiting I/O. The translations
editor uses this during bulk document reconciliation and command result updates.

## Structured validation

Models and nested DTOs may implement `INotifyDataErrorInfo`. Generated snapshots
then expose `validation.hasErrors`, `validation.truncated`, and `validation.errors`, alongside existing
property error string arrays. Each error contains a `path` of wire property
names, list indexes, or dictionary keys and a `message`. Root entity errors use
an empty path. Aliases are preserved, so a postal-code error might have the path
`["profile", "postal-code"]`.

Return a `BridgeValidationMessage` from `GetErrors` to supply a stable `Code`,
`Severity`, or relative CLR `MemberPaths`. Ordinary strings and
`ValidationResult` remain supported. Generated metadata resolves paths without
runtime reflection. Traversal and messages are bounded; `truncated` reports when
validation details are incomplete. Nested `ErrorsChanged` notifications publish new validation
state, and removed objects lose their subscriptions. This projection works
across model frameworks; it does not run validation rules itself.

Use `[RunicIgnore]` on validation-only DTO properties such as a computed
`HasErrors`; their values are represented by the validation projection.

## Incremental generation

The generator caches successful multi-view generation in its C# `obj` output
directory. It hashes the model/dependency assemblies, generator assemblies,
output-affecting options, and generated output contents. Missing or edited
outputs invalidate the cache; unchanged generated files keep their timestamps.
Set `RUNIC_BRIDGE_CODEGEN_FORCE=1` or `RUNIC_BRIDGE_CODEGEN_CACHE=0` to bypass it.

This skips redundant bridge generation, not application compilation or the
configured frontend build command. Custom frontend commands may consume inputs
unknown to the bridge generator, so their existing build semantics are preserved.
