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
plain TypeScript, Svelte, and Angular can use the same contract.

Start with the [first Window](../../../examples/first-window/README.md), then read
the [Toolkit Notes](../../../examples/notes-view-first/README.md) and
[Reactive Notes](../../../examples/notes-reactive-views/README.md) examples. For
installation and templates, see [getting started](getting-started/README.md).
The package API and build details are in
[`Runic.Application`](../../../packages/dotnet/Runic.Application.Views/README.md).
