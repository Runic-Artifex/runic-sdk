# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

| ID | API family | Packages |
|---|---|---|
| [`RUNICASSETS001`](#RUNICASSETS001) | Linux development-directory asset source | `Runic.Assets` |
| [`RUNICDESK100`](#RUNICDESK100) | Custom window host providers | `Runic.Desktop`, `Runic.Desktop.Gtk4` |
| [`RUNICDESK101`](#RUNICDESK101) | Low-level presentation transport | `Runic.Desktop` |

An ID is `RUNIC`, the package family's prefix and a three-digit number. A prefix
that names no other diagnostics numbers its experimental families from `001`,
such as `RUNICASSETS001`. A prefix that already names compiler or build
diagnostics numbers them from `100`, so the two never share a number: the D-Bus
source generator reports `RUNICDBUS001` to `RUNICDBUS012`, and an experimental
D-Bus family would be `RUNICDBUS100`. No D-Bus API is experimental today. The
Runic.Desktop build targets report `RUNICDESK001` and `RUNICDESK002`, so the
experimental Desktop families are `RUNICDESK100` and `RUNICDESK101`.

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

<a id="RUNICWPF001"></a><a id="RUNICNAV001"></a>

| ID | API family | Graduated in |
|---|---|---|
| `RUNICWPF001` | WPF child web presentation (`Runic.Application.Views.Wpf`): `RunicViewHost`, `RunicViewHostState`, `RunicWebView`, `WpfBridgeView<TViewModel>`, `WpfBridgeViewExtensions` and `WpfPlatformServices`. `RunicWebView.CreateWindowHostFactory()` joined [`RUNICDESK100`](#RUNICDESK100), the custom window-host family it belongs to. | 0.9.0-preview.1 |
| `RUNICNAV001` | Navigation (`Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`, `Runic.Navigation.ReactiveUI.Reactive`) | 0.7.0-preview.7 |

<a id="RUNICASSETS001"></a>

## RUNICASSETS001

**Covers:** the Linux development-directory source of `Runic.Assets`:
`DevelopmentDirectoryAssetSource`, `IAssetSourceChangeNotifier`, `IAssetWatch`,
`AssetWatchOptions` and `AssetSourceChangedEventArgs`.

**Status:** experimental since 0.9.0-preview.1. The source refreshes a local
directory for a development inner loop, but no Runic host or tool uses it yet:
`dotnet runic dev` serves the frontend from its development server. Its shape,
such as the synchronous `Refresh`, the two ways to stop a watch and the
Linux-only name, may change once a host consumes it. It graduates when a Runic
host uses it. Embedded and archive sources (`EmbeddedAssetSource`,
`AssetArchiveSource`) are stable.

<a id="RUNICDESK100"></a>

## RUNICDESK100

**Covers:** custom window hosts for Runic Desktop, the provider interface that the
GTK 4 provider and the WPF control implement: `IDesktopWindowHostFactory`,
`IDesktopWindowHost`, `IDesktopEventLoopWindowHostFactory`,
`ILinuxDesktopWindowHostFactory`, `IDesktopNativeDispatchWindowHost`,
`IDesktopWindowStateHost`, `IDesktopFileDropWindowHost`,
`DesktopWindowHostOptions`, `DesktopHostOptions.WindowHostFactory`,
`LinuxDesktopRuntime.ClaimBackend` and `LinuxDesktopRuntime.IsLibraryAvailable`
in `Runic.Desktop`, `Gtk4WindowHostFactory` in `Runic.Desktop.Gtk4`, and
`RunicWebView.CreateWindowHostFactory()` in `Runic.Application.Views.Wpf`.

**Status:** experimental since 0.9.0-preview.1. Applications that use the
built-in windows, or select GTK 4 with `WithGtk4()`, do not use this family and
need no suppression; `LinuxDesktopRuntime.CanUse` stays stable. Neither do WPF
applications that use `RunicViewHost`; only composing a `RunicWebView` with your
own `DesktopHost` uses this family. A window host
must follow threading and credential rules that are still changing:
`DocumentStartScript` carries the session credential, and each new window
capability so far has been another optional interface. The family graduates
once a window host outside Runic exists, or the CS-WebUI and WPF hosts have
settled the interface.

<a id="RUNICDESK101"></a>

## RUNICDESK101

**Covers:** the low-level presentation transport of a `DesktopSurface`:
`RegisterCapability`, `SubscribeConnectionEvents`, `ConnectionCount`,
`RunJavaScriptAsync`, `ExecuteJavaScriptAsync`, `NavigateAsync` and `SendAsync`,
and the types `PresentationCapabilityHandler`,
`PresentationCapabilityRegistration`, `PresentationEventKind`,
`PresentationInvocation`, `PresentationResult` and `PresentationSession`.

**Status:** experimental since 0.9.0-preview.1. Runic Windows and Views
(`Runic.Application.Views.Desktop`) carry their Bridge over this transport, so a
Views application does not use it directly. Its shape follows the WebUI wire
protocol (event kinds numbered like WebUI's, arguments read by position, `ulong`
session IDs) and mixes `Task` and `ValueTask`, which a later transport design can
change. It graduates with that redesign.

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
