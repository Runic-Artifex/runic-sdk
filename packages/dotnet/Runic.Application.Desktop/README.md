# Runic.Application.Desktop

Present Runic Windows and Views with
[Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md):
native windows, embedded WebViews or browser presentations, and the Runic
platform services.

```sh
dotnet add package Runic.Application.Desktop --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

New projects can start with this host: `dotnet new runic-app --host desktop`.

In a project named `MyApp`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyApp; // the generated AddRunicViews()
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddScoped<MainViewModel>();
services.AddRunicViews();
await using var provider = services.BuildServiceProvider();
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
{
    // Windows uses WebView2 and macOS WKWebView; Linux selects a toolkit explicitly.
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
});
var windowOptions = new DesktopWindowOptions { Width = 800, Height = 600 };
// Stops at startup when a registration, WebView runtime or window option is missing.
provider.ValidateDesktopWindow<MainViewModel>(desktop, windowOptions).ThrowIfInvalid();
await using var window = await provider.OpenDesktopWindowAsync<MainWindow, MainViewModel>(
    desktop,
    new DesktopSurfaceOptions { Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html") },
    host => new MainWindow(host),
    windowOptions);
window.Presentation.WaitForClose();

public sealed partial class MainWindow(DesktopBridgeWindow<MainViewModel> host)
    : RunicWindow<MainViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}
```

## Entry points

- `OpenDesktopWindowAsync<TWindow, TViewModel>` creates the Window's DI scope,
  resolves its ViewModel, opens a `DesktopSurface` and its presentation, and
  attaches the generated Bridge.
- `ValidateDesktopWindow<TViewModel>` checks, before any window opens, that the
  generated Bridge for the ViewModel is registered and that
  `DesktopHost.Validate` accepts the window options. It returns every problem
  as a `DesktopDiagnostic` with a stable code and remediation: an error
  `bridge-not-registered`, the
  [Runic Desktop codes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md),
  and a warning `viewmodel-not-registered` when the container does not report
  the ViewModel through `IServiceProviderIsService`. A container can resolve
  services it does not report, so only the Bridge check fails; a container
  without `IServiceProviderIsService` is not checked. `ThrowIfInvalid()` throws
  a `DesktopConfigurationException`. `OpenDesktopWindowAsync` runs the Bridge
  check itself before it creates a surface, so a missing `AddRunicViews()`
  fails with that exception instead of a dependency-injection error. A missing
  Bridge is logged as event 2001 through the provider's `ILoggerFactory`.
- `DesktopBridgeWindow<TViewModel>` owns the Window's scope, surface, and attachment.
  It exposes `ViewModel`, `Surface`, `Presentation`, and `CloseAsync`, which
  stops new operations and waits for accepted ones before releasing the scope.
  It implements `IBridgeWindow`, the lifetime contract shared with the CS-WebUI
  host, and returns the same `BridgeWindowCloseResult`.
- `DesktopBridgeWindow<TViewModel>.NativeOwner` (a `DesktopNativeOwner`) is the
  presentation's `INativePickerOwner`. Pass it to the platform provider that
  matches the window's backend; no custom adapter is needed:
  - Windows: `WindowsPlatformProvider.CreateFileDialogs(owner)`.
  - macOS: `MacOSPlatformProvider.CreateFileDialogs(owner)`.
  - Linux with GTK 3:
    `LinuxPlatformProvider.CreateFileDialogs(owner)`.
  - Linux with GTK 4:
    `PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(owner))`.
    `LinuxPlatformProvider` parents dialogs through GTK 3, so do not use it
    with a GTK 4 window.

  The owner runs provider callbacks on the window's native thread and becomes
  unavailable when the window closes or the surface replaces it. A presentation
  without native dispatch still has an owner, but it reports
  `IsAvailable == false`. Examples are an installed browser after an
  embedded-window fallback, or a custom host without a native handle.
  Applications that open a `DesktopWindow` themselves create one with
  `new DesktopNativeOwner(window)`.
- `DesktopBridgeTransport` connects a `DesktopSurface` to a
  `WindowContentSession` directly, for applications that compose a surface
  themselves.

The surface owns authenticated browser or embedded WebView sessions and
removable capability bindings; the application Window owns its ViewModel scope
and logical View lifetimes. `DesktopHostOptions` and `DesktopWindowOptions`
select the browser or embedded backend; the Runic Desktop guide lists the
native prerequisites for each platform.

The package's build targets copy `runic-desktop-views.js` into `www/`. Load
`webui.js` (served by the surface) and `runic-desktop-views.js` before the
generated client in `index.html`. The adapter uses the same host-neutral client
contract as the CS-WebUI adapter, and its build defaults match
`Runic.Application.CsWebUi`, so `AddRunicViews()` is generated in
`<ProjectName>.RunicBridgeComposition`.

The [First Window on Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window-desktop)
example is a complete application. See the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/)
to choose between this host and CS-WebUI.

## Automation

Set `RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` to start a template-shaped
application without a user. With it set, `OpenDesktopWindowAsync` closes every
window it opens, as soon as the presentation has opened and, with
`DesktopHostOptions.WaitForConnection`, its bridge has connected. Before
closing, it writes:

- a warning to standard error, and event 2002 through a registered
  `ILoggerFactory`, so an inherited setting is visible;
- `RUNIC_APPLICATION_OPENED=<presentation>` (for example `Embedded`, or the
  browser it fell back to) to standard output.

An application shaped like the `runic-app` template then sees `WaitForClose`
return and exits normally. Applications that keep running after a window
closes, or that open further windows, are not meant to use it. This is the
Desktop counterpart of CS-WebUI's `RUNIC_APPLICATION_SERVE_ONLY`. The template
acceptance check uses it to run the GTK 4 template under Xvfb.
