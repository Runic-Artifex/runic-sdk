# Runic Application Views

Runic Application Views composes explicit .NET Windows and logical Views with
ordinary TypeScript clients. A `RunicWindow<TViewModel>` owns a window context;
a `RunicView<TViewModel>` declares a typed presentation contract. The build
inspects the compiled application after its MVVM source generators run and emits
C# attachments and TypeScript modules.

The browser framework owns the visual tree. .NET owns ViewModel scopes, View
construction, typed commands, property writes, and operation lifetimes. A View
mount is acknowledged by the browser, so a content session can create and release
its logical View as frontend routes change. Generated clients are framework-neutral;
React, Vue, Svelte, Angular, and plain TypeScript use the same contract. They
share one browser runtime, [`@runic-artifex/views`](../../../packages/web/views/README.md),
which also provides a mock Bridge for development without .NET. The
[React](../../../packages/web/react/README.md) and
[Vue](../../../packages/web/vue/README.md) packages, `useView` in
[`@runic-artifex/svelte/views`](../../../packages/web/svelte/README.md) and
`injectView()` in [`@runic-artifex/angular`](../../../packages/web/angular/README.md)
connect and dispose clients with component lifetimes.

Start with [getting started](getting-started/README.md) or the
[first Window](../../../examples/first-window/README.md), then read the
[CommunityToolkit Notes](../../../examples/notes-view-first/README.md) and
[Reactive Notes](../../../examples/notes-reactive-views/README.md) examples. The
package API and build properties are in the
[`Runic.Application` package guide](../../../packages/dotnet/Runic.Application.Views/README.md).
The package is built from `packages/dotnet/Runic.Application.Views`, and its
types are in the `Runic.Application.Views` namespace; the
[reference](reference/README.md) lists every package with its source folder.

## Upgrading generated clients

Rebuild after upgrading so the C# bridges and TypeScript clients regenerate
together, and add `@runic-artifex/views` to the frontend's dependencies.

- Connected client types are named `<Name>Client`. The former `<Name>View`
  name remains as a deprecated alias.
- Import `BridgeError`, `BridgeOperation*` and `FieldBaseline*` from
  `@runic-artifex/views`. Generated modules no longer export them, so
  `instanceof BridgeError` holds across modules.
- Generated state types no longer contain `revision`.
- `snapshot` returns the last state after disposal, and `subscribe` after
  disposal delivers that state once and returns a no-op unsubscribe.
- Generated C# files are named `<FullName>.Bridge.g.cs` and
  `<FullName>.View.g.cs`. The build removes the former files automatically.
- `DateTimeOffset` values keep their offset on the wire, and outbound date values
  must be ISO 8601 strings.
- Toolkit asynchronous commands and ReactiveUI commands with an argument publish
  `is<Name>Executing`; nullable ReactiveUI command and interaction types are
  `T | null`.
