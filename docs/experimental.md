# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

| ID | API family | Packages |
|---|---|---|
| [`RUNICWPF001`](#RUNICWPF001) | WPF child web presentation | `Runic.Application.Views.Wpf` |
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
  <NoWarn>$(NoWarn);RUNICWPF001</NoWarn>
</PropertyGroup>
```

Or suppress it where you use the API, for a file or a region of one:

```csharp
#pragma warning disable RUNICWPF001 // The WPF web View host is experimental.
RunicViewHost.SetServices(mainWindow, services);
#pragma warning restore RUNICWPF001
```

Code that the WPF markup compiler generates for you needs no suppression.
`Runic.Application.Views.Wpf` suppresses its ID in the project's intermediate
`.g.cs` files, where WPF declares a named control such as
`<rv:RunicViewHost x:Name="Editor" />`. The diagnostic is reported only in your
own code, so a per-site suppression is enough with
`TreatWarningsAsErrors`.

Navigation (`Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`
and `Runic.Navigation.ReactiveUI.Reactive`) is no longer experimental as of
0.7.0-preview.7. Its former diagnostic ID is retired; remove its suppressions.

<a id="RUNICWPF001"></a>

## RUNICWPF001

**Covers:** the WPF child presentation of a Runic web View in
`Runic.Application.Views.Wpf`: `RunicViewHost`, `RunicViewHostState`,
`RunicWebView`, `WpfBridgeView<TViewModel>`, `WpfBridgeViewExtensions`
(`CreateWpfViewAsync`) and `WpfPlatformServices`.

**Status:** experimental. It hosts Runic Desktop's WebView2 controller inside an
existing WPF visual tree, and its lifecycle API is still being shaped by the
[hybrid WPF example](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-hybrid-editor/README.md).
It is planned to graduate before 1.0, once these are done: keyboard focus can
leave the web View with Tab in both directions (done in 0.9), a Narrator check,
a smoke test of two hosts in one window, and the remaining API changes of the
review (`OpenAsync` without its options parameter, a cancellation token on
`ReloadAsync` and `WpfPlatformServices.ForWindow(Window)`; `StartPage`,
`ViewModel`, the shared `Services` and the removal of the public `DiagnosticId`
are done in 0.9). Graduation removes the attribute and retires the ID.

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
in `Runic.Desktop`, and `Gtk4WindowHostFactory` in `Runic.Desktop.Gtk4`.

**Status:** experimental since 0.9.0-preview.1. Applications that use the
built-in windows, or select GTK 4 with `WithGtk4()`, do not use this family and
need no suppression; `LinuxDesktopRuntime.CanUse` stays stable. A window host
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
