# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

| ID | API family | Packages |
|---|---|---|
| [`RUNICASSETS001`](#RUNICASSETS001) | Linux development-directory asset source | `Runic.Assets` |

An ID is `RUNIC`, the package family's prefix and a three-digit number. A prefix
that names no other diagnostics numbers its experimental families from `001`,
such as `RUNICASSETS001`. A prefix that already names compiler or build
diagnostics numbers them from `100`, so the two never share a number: the D-Bus
source generator reports `RUNICDBUS001` to `RUNICDBUS012`, and an experimental
D-Bus family would be `RUNICDBUS100`. No D-Bus API is experimental today. The
Runic.Desktop build targets report `RUNICDESK001` and `RUNICDESK002`, so the
experimental Desktop families number from `RUNICDESK100`.

## Suppressing a diagnostic

Suppress the ID for a whole project, for example in its `.csproj` or a
`Directory.Build.props` that also covers its test project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RUNICASSETS001</NoWarn>
</PropertyGroup>
```

Or suppress it where you use the API, for a file or a region of one:

```csharp
#pragma warning disable RUNICASSETS001 // The development-directory source is experimental.
using var source = new DevelopmentDirectoryAssetSource(frontendDirectory, "index.html");
#pragma warning restore RUNICASSETS001
```

<a id="retired"></a>

## Graduated and retired IDs

When a family graduates, its attribute is removed and its API is supported like
the rest of its package. Its ID is retired and never names another family, so a
leftover suppression is harmless; remove it.

<a id="RUNICDESK101"></a><a id="RUNICDESK100"></a><a id="RUNICWPF001"></a><a id="RUNICNAV001"></a>

| ID | API family | Graduated in |
|---|---|---|
| `RUNICDESK101` | Low-level presentation transport (`Runic.Desktop`), partly graduated and the rest made internal. Stable: `DesktopSurface.ExecuteJavaScriptAsync` (now returning `ValueTask<string>`), `DesktopSurface.ConnectionCount` and `DesktopSurface.SubscribeConnectionEvents`, whose handler receives the new `DesktopConnectionEvent` (`Kind`, `Surface`) with `DesktopConnectionEventKind` instead of `PresentationInvocation`. Internal: `RegisterCapability`, `RunJavaScriptAsync`, `NavigateAsync`, `SendAsync` and the `Presentation*` types; calls between JavaScript and .NET go through [Runic Windows and Views](../packages/dotnet/Runic.Application.Views.Desktop/README.md). | 0.10.0 |
| `RUNICDESK100` | Custom window hosts (`Runic.Desktop`, `Runic.Desktop.Gtk4`, `Runic.Application.Views.Wpf`): `IDesktopWindowHostFactory`, `IDesktopWindowHost`, `IDesktopEventLoopWindowHostFactory`, `ILinuxDesktopWindowHostFactory`, `IDesktopNativeDispatchWindowHost`, `IDesktopWindowStateHost`, `IDesktopFileDropWindowHost`, `DesktopWindowHostOptions`, `DesktopHostOptions.WindowHostFactory`, `LinuxDesktopRuntime.ClaimBackend`, `LinuxDesktopRuntime.IsLibraryAvailable`, `Gtk4WindowHostFactory` and `RunicWebView.CreateWindowHostFactory()`. What a host must do is in the Runic.Desktop README, [Custom window hosts](../packages/dotnet/Runic.Desktop/README.md#custom-window-hosts). | 0.10.0 |
| `RUNICWPF001` | WPF child web presentation (`Runic.Application.Views.Wpf`): `RunicViewHost`, `RunicViewHostState`, `RunicWebView`, `WpfBridgeView<TViewModel>`, `WpfBridgeViewExtensions` and `WpfPlatformServices`. `RunicWebView.CreateWindowHostFactory()` joined `RUNICDESK100`, the custom window-host family it belongs to. | 0.9.0 |
| `RUNICNAV001` | Navigation (`Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`, `Runic.Navigation.ReactiveUI.Reactive`) | 0.7.0-preview.7 |

`Runic.Platform.Administration.Windows` is no longer experimental as of 0.10.0.
Its experimental status was stated only in its README; it never had a diagnostic
ID, so there is no suppression to remove.

<a id="RUNICASSETS001"></a>

## RUNICASSETS001

**Covers:** the Linux development-directory source of `Runic.Assets`:
`DevelopmentDirectoryAssetSource`, `IAssetSourceChangeNotifier`, `IAssetWatch`,
`AssetWatchOptions` and `AssetSourceChangedEventArgs`.

**Status:** experimental since 0.9.0. The source refreshes a local
directory for a development inner loop, but no Runic host or tool uses it yet:
`dotnet runic dev` serves the frontend from its development server. Its shape,
such as the synchronous `Refresh`, the two ways to stop a watch and the
Linux-only name, may change once a host consumes it. It graduates when a Runic
host uses it. Embedded and archive sources (`EmbeddedAssetSource`,
`AssetArchiveSource`) are stable.

<a id="npm-packages"></a>

## npm packages

An npm package has no diagnostic ID: its experimental APIs are exported only
from its `./experimental` subpath, so importing one is the opt-in. They may
change or be removed in a minor release, and no stable export names an
experimental type. Graduation moves the export to the package's root entry; the
experimental path is then removed in the next major. The APIs today:

| Package | `./experimental` exports | Status |
|---|---|---|
| `@runic-artifex/views` | `createCollectionViewportController`, `collectionViewport`, `CollectionViewport`, `CollectionViewportController`, `CollectionViewportOptions` | The fixed-row-height viewport model has one consumer, the [DynamicData example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/dynamicdata); variable row heights may change its shape. |
| `@runic-artifex/views` | `createLatestOperationController`, `LatestOperationController`, `LatestOperationIntent`, `LatestOperationState` | No framework adapter wraps it yet, and its shape differs from `createOperationController` (`run(intent)`, `clear(mode)`). It graduates once an adapter and a sample use it. |
| `@runic-artifex/views-effect` | `followViewport`, `viewportChanges`, `ViewportRange` | Effect helpers of the viewport controller; they follow its status. |
| `@runic-artifex/react` | `useCollectionViewport`, `CollectionViewportHandle` | Wraps the viewport controller; follows its status. |
| `@runic-artifex/react` | `useSuspenseView`, `retrySuspenseView`, `SuspenseViewHandle` | The module-wide connection cache and its fixed ten-second timeout may change; no sample uses it yet. |
| `@runic-artifex/vue`, `@runic-artifex/svelte` | `useCollectionViewport`, `CollectionViewportHandle` | Wrap the viewport controller; follow its status. |
| `@runic-artifex/angular` | `injectCollectionViewport`, `InjectCollectionViewportOptions` | Wraps the viewport controller; follows its status. |
