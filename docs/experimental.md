# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

No .NET API is experimental. When one is, it is listed here with its ID, API
family and packages.

An ID is `RUNIC`, the package family's prefix and a three-digit number. A prefix
that names no other diagnostics numbers its experimental families from `001`,
such as `RUNICASSETS001`. A prefix that already names compiler or build
diagnostics numbers them from `100`, so the two never share a number: the D-Bus
source generator reports `RUNICDBUS001` to `RUNICDBUS013`, and an experimental
D-Bus family would be `RUNICDBUS100`. No D-Bus API is experimental today. The
Runic.Desktop build targets report `RUNICDESK001` and `RUNICDESK002`, so the
experimental Desktop families number from `RUNICDESK100`. The Navigation
ReactiveUI build targets report `RUNICNAV002`, after the retired `RUNICNAV001`,
so an experimental Navigation family would be `RUNICNAV100`.

## Suppressing a diagnostic

Suppress the ID for a whole project, for example in its `.csproj` or a
`Directory.Build.props` that also covers its test project. With the ID
`RUNICDBUS100`, which an experimental D-Bus family would have:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RUNICDBUS100</NoWarn>
</PropertyGroup>
```

Or suppress it where you use the API, for a file or a region of one:

```csharp
#pragma warning disable RUNICDBUS100 // The API is experimental.
// The code that uses the experimental API.
#pragma warning restore RUNICDBUS100
```

<a id="experimental-packages"></a>

## Experimental packages

A whole package can be experimental. It is not part of the release inventory,
and its build targets report its ID as an error, as the compiler does for an
experimental API, until the project suppresses it.

<a id="RUNICDESK102"></a>

## RUNICDESK102

The native Desktop server, `Runic.Desktop.NativeServer` (X1). Referencing the
package serves Runic Desktop surfaces from a native Rust (axum) HTTP and
WebSocket server instead of ASP.NET Core Kestrel (`Runic.Desktop.Kestrel`): the
same Views protocol, admission, content and bootstrap code run over it, and the
application needs no ASP.NET Core. With both server packages referenced, set
`RunicDesktopServer` to `Kestrel` to build the same project on Kestrel; any
other value than `Native`, `Kestrel` or `None` fails with
[`RUNICDESK003`](diagnostics.md#runicdesk003). See the
[package README](../packages/dotnet/Runic.Desktop.NativeServer/README.md) for
the supported runtime identifiers and what differs from Kestrel.

<a id="retired"></a>

## Graduated and retired IDs

When a family graduates, its attribute is removed and its API is supported like
the rest of its package. Its ID is retired and never names another family, so a
leftover suppression is harmless; remove it.

<a id="RUNICASSETS001"></a><a id="RUNICDESK101"></a><a id="RUNICDESK100"></a><a id="RUNICWPF001"></a><a id="RUNICNAV001"></a>

| ID | API family | Graduated in |
|---|---|---|
| `RUNICASSETS001` | Development-directory asset source (`Runic.Assets`): `DevelopmentDirectoryAssetSource`, `IAssetSourceChangeNotifier`, `AssetWatchOptions` and `AssetSourceChangedEventArgs`, now on Linux, Windows and macOS. `Refresh` became `RefreshAsync`; `StartWatching(options)` returns nothing and the watch runs until the source is disposed, so `IAssetWatch` was removed. `dotnet runic dev` uses it when no Vite or Angular development server runs: the Runic Desktop window serves the frontend output directory and reloads when it changes ([dotnet-runic](../tools/dotnet-runic/README.md)). | 0.10.0 |
| `RUNICDESK101` | Low-level presentation transport (`Runic.Desktop`), partly graduated and the rest made internal. Stable: `DesktopSurface.ExecuteJavaScriptAsync` (now returning `ValueTask<string>`), `DesktopSurface.ConnectionCount` and `DesktopSurface.SubscribeConnectionEvents`, whose handler receives the new `DesktopConnectionEvent` (`Kind`, `Surface`) with `DesktopConnectionEventKind` instead of `PresentationInvocation`. Internal: `RegisterCapability`, `RunJavaScriptAsync`, `NavigateAsync`, `SendAsync` and the `Presentation*` types; calls between JavaScript and .NET go through [Runic Windows and Views](../packages/dotnet/Runic.Application.Views.Desktop/README.md). | 0.10.0 |
| `RUNICDESK100` | Custom window hosts (`Runic.Desktop`, `Runic.Desktop.Gtk4`, `Runic.Application.Views.Wpf`): `IDesktopWindowHostFactory`, `IDesktopWindowHost`, `IDesktopEventLoopWindowHostFactory`, `ILinuxDesktopWindowHostFactory`, `IDesktopNativeDispatchWindowHost`, `IDesktopWindowStateHost`, `IDesktopFileDropWindowHost`, `DesktopWindowHostOptions`, `DesktopHostOptions.WindowHostFactory`, `LinuxDesktopRuntime.ClaimBackend`, `LinuxDesktopRuntime.IsLibraryAvailable`, `Gtk4WindowHostFactory` and `RunicWebView.CreateWindowHostFactory()`. What a host must do is in the Runic.Desktop README, [Custom window hosts](../packages/dotnet/Runic.Desktop/README.md#custom-window-hosts). | 0.10.0 |
| `RUNICWPF001` | WPF child web presentation (`Runic.Application.Views.Wpf`): `RunicViewHost`, `RunicViewHostState`, `RunicWebView`, `WpfBridgeView<TViewModel>`, `WpfBridgeViewExtensions` and `WpfPlatformServices`. `RunicWebView.CreateWindowHostFactory()` joined `RUNICDESK100`, the custom window-host family it belongs to. | 0.9.0 |
| `RUNICNAV001` | Navigation (`Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`, `Runic.Navigation.ReactiveUI.Reactive`) | 0.7.0-preview.7 |

`Runic.Platform.Administration.Windows` is no longer experimental as of 0.10.0.
Its experimental status was stated only in its README; it never had a diagnostic
ID, so there is no suppression to remove.

<a id="npm-packages"></a>

## npm packages

An npm package has no diagnostic ID: an experimental API is exported only from
the package's `./experimental` subpath, so importing one is the opt-in. It may
change or be removed in a minor release, and no stable export names an
experimental type. Graduation moves the export to the package's root entry.
Before 1.0 it is removed from `./experimental` in the same minor release; from
1.0 the experimental export remains as a deprecated alias until the next major.

No npm API is experimental, and no package has an `./experimental` entry. The
APIs below graduated in 0.10.0, which removed the `./experimental` entries of all
six packages. Import them from the package root: an `./experimental` import fails
with `ERR_PACKAGE_PATH_NOT_EXPORTED` (Node) or TS2307 (TypeScript).

| Package | Graduated exports | Graduated in |
|---|---|---|
| `@runic-artifex/views` | `collectionViewport`, `createCollectionViewportController`, `CollectionViewport`, `CollectionViewportController`, `CollectionViewportOptions`. The viewport keeps a fixed row height; variable heights would be a separate factory. | 0.10.0 |
| `@runic-artifex/views` | `createLatestOperationController`, `LatestOperationController`, `LatestOperationIntent`, `LatestOperationState`. React, Vue and Svelte `useLatestOperation` and Angular `injectLatestOperation` wrap it, and the [operations package consumer](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/fixtures/application/operations-consumer) is its sample. | 0.10.0 |
| `@runic-artifex/views-effect` | `followViewport`, `viewportChanges`, `ViewportRange` | 0.10.0 |
| `@runic-artifex/react` | `useCollectionViewport`, `CollectionViewportHandle` | 0.10.0 |
| `@runic-artifex/react` | `useSuspenseView`, `retrySuspenseView`, `SuspenseViewHandle`. `useSuspenseView` takes a new `{ releaseTimeout }` option (`SuspenseViewOptions`) instead of the fixed ten seconds. | 0.10.0 |
| `@runic-artifex/vue`, `@runic-artifex/svelte` | `useCollectionViewport`, `CollectionViewportHandle` | 0.10.0 |
| `@runic-artifex/angular` | `injectCollectionViewport`, `InjectCollectionViewportOptions` | 0.10.0 |

