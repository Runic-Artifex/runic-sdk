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
