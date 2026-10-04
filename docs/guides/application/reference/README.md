# Reference

Each package is named for what you install; its source folder and namespace
keep the older `Views` name.

| Package                                                                                                    | Source folder                                        | Namespace                            |
| ---------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- | ------------------------------------ |
| [`Runic.Application`](../../../../packages/dotnet/Runic.Application.Views/README.md)                       | `packages/dotnet/Runic.Application.Views`            | `Runic.Application.Views`            |
| [`Runic.Application.CsWebUi`](../../../../packages/dotnet/Runic.Application.Views.CsWebUi/README.md)       | `packages/dotnet/Runic.Application.Views.CsWebUi`    | `Runic.Application.Views.CsWebUi`    |
| [`Runic.Application.Desktop`](../../../../packages/dotnet/Runic.Application.Desktop/README.md)             | `packages/dotnet/Runic.Application.Desktop`          | `Runic.Application.Views.Desktop`    |
| [`Runic.Application.ReactiveUI`](../../../../packages/dotnet/Runic.Application.Views.ReactiveUI/README.md) | `packages/dotnet/Runic.Application.Views.ReactiveUI` | `Runic.Application.Views.ReactiveUI` |
| [`Runic.Application.Testing`](../../../../packages/dotnet/Runic.Application.Testing/README.md)             | `packages/dotnet/Runic.Application.Testing`          | `Runic.Application.Testing`          |

- [ReactiveUI 26 capabilities and Avalonia comparison](reactiveui.md)
- [Views build targets](../../../../packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets), shipped in the `Runic.Application` package
- [`dotnet runic`](../../../../tools/dotnet-runic/README.md) development and doctor commands
- [`@runic-artifex/views`](../../../../packages/web/views/README.md): the shared browser runtime and mock Bridge for generated clients
- [React package](../../../../packages/web/react/README.md), [Vue package](../../../../packages/web/vue/README.md), [Svelte package](../../../../packages/web/svelte/README.md) and [Angular package](../../../../packages/web/angular/README.md)

The examples show complete Window composition, generated client use, browser
mount lifecycle, and frontend integration.
