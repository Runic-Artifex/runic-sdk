# Runic.Application.Desktop

Present Runic Windows and Views with
[Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md):
native windows, embedded WebViews or browser presentations, and the Runic
platform services.

```sh
dotnet add package Runic.Application.Desktop --prerelease
```

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
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions());
await using var window = await provider.OpenDesktopWindowAsync<MainWindow, MainViewModel>(
    desktop,
    new DesktopSurfaceOptions { RootFolder = Path.Combine(AppContext.BaseDirectory, "www"), Content = "index.html" },
    host => new MainWindow(host),
    new DesktopWindowOptions { Width = 800, Height = 600 });
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
- `DesktopBridgeWindow<TViewModel>` owns that scope, surface, and attachment.
  It exposes `ViewModel`, `Surface`, `Presentation`, and `CloseAsync`, which
  stops new operations and waits for accepted ones before releasing the scope.
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
[host selection guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/desktop/host-selection.md)
to choose between this host and CS-WebUI.
