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
React, Vue, Svelte, Angular, and plain TypeScript use the same contract.

Start with [getting started](getting-started/README.md) or the
[first Window](../../../examples/first-window/README.md), then read the
[CommunityToolkit Notes](../../../examples/notes-view-first/README.md) and
[Reactive Notes](../../../examples/notes-reactive-views/README.md) examples. The
package API and build properties are in the
[`Runic.Application` package guide](../../../packages/dotnet/Runic.Application.Views/README.md).
The package is built from `packages/dotnet/Runic.Application.Views`, and its
types are in the `Runic.Application.Views` namespace; the
[reference](reference/README.md) lists every package with its source folder.
