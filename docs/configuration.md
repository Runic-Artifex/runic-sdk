# Configuration and settings

Runic has no configuration or settings API. `RunicDesktopHost.Run` and
`RunicCsWebUiHost.Run` take any `IServiceProvider`, so an application configures
itself with standard .NET and passes its services to the host: a plain
`ServiceCollection`, as the `runic-app` template does, or Generic Host
(`Microsoft.Extensions.Hosting`) with `Microsoft.Extensions.Configuration`,
options and hosted services. This guide shows the Generic Host setup that suits a
desktop application, how to ship configuration files, and where to store what the
user changes.

## Generic Host with a Runic host

A Runic Desktop application on `Runic.Desktop.Kestrel` already has
`Microsoft.Extensions.Hosting`: Kestrel references the ASP.NET Core shared
framework, which contains it. An application on the native server
(`Runic.Desktop.NativeServer`) or on CS-WebUI adds the
[Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting)
package.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

var settings = new HostApplicationBuilderSettings
{
    Args = args,
    // The application's directory, not the working directory.
    ContentRootPath = AppContext.BaseDirectory,
    Configuration = new ConfigurationManager(),
};
// Read appsettings.json once at start instead of watching it.
settings.Configuration.AddInMemoryCollection([new("hostBuilder:reloadConfigOnChange", "false")]);
var builder = Host.CreateApplicationBuilder(settings);
// Runic Desktop handles Ctrl+C, SIGTERM and SIGHUP itself.
builder.Services.AddSingleton<IHostLifetime, DesktopLifetime>();
builder.Services.Configure<EditorOptions>(builder.Configuration.GetSection("Editor"));
builder.Services.AddHostedService<SyncService>();
builder.Services.AddScoped<MainViewModel>();
builder.Services.AddRunicViews();
using var host = builder.Build();

// No await before Run: it needs the process main thread.
return RunicDesktopHost.Run(host.Services, async runic =>
{
    await host.StartAsync();
    try
    {
        await using var window = await runic.OpenWindowAsync<MainWindow>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var stopping = lifetime.ApplicationStopping.Register(
            () => _ = window.CloseAsync(TimeSpan.FromSeconds(5)).AsTask());
        await window.WaitForCloseAsync();
        return 0;
    }
    finally
    {
        await host.StopAsync();
    }
});

sealed class DesktopLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- **Content root.** Generic Host's default content root is the working
  directory. A desktop application started from a launcher, Finder or a terminal
  in `$HOME` would look for `appsettings.json` there, and with reloading on,
  watch that whole directory tree, which can exhaust Linux's inotify watches
  ([#606](https://github.com/Runic-Artifex/runic-sdk/issues/606)).
  `ContentRootPath = AppContext.BaseDirectory` reads the files next to the
  application wherever it was started.
- **No reloading.** `hostBuilder:reloadConfigOnChange` set to `false` makes the
  default sources, `appsettings.json`, `appsettings.{Environment}.json`,
  `<ApplicationName>.settings.json` and `<ApplicationName>.settings.{Environment}.json`
  in the content root, and user secrets in Development, read their files once.
  A source you add yourself takes `reloadOnChange: false`, for example
  `AddJsonFile(path, optional: true, reloadOnChange: false)`. Settings that the
  user changes while the application runs belong in [per-user settings](#per-user-settings),
  which the application reads and writes itself.
- **Signals.** Generic Host's default `ConsoleLifetime` cancels Ctrl+C and
  SIGTERM and only calls `StopApplication`. Runic Desktop handles these signals:
  it closes the open windows, lets a second Ctrl+C end a shutdown that hangs, and
  leaves the default action to a signal that arrives while no window is open.
  The `IHostLifetime` above does nothing, so Runic Desktop keeps them. CS-WebUI
  handles no signals; keep `ConsoleLifetime` there, and Ctrl+C reaches the
  application through `ApplicationStopping`.
- **Order.** Build the host synchronously and call `Run` before any `await`.
  `host.StartAsync()` inside the callback starts the hosted services on the
  running event loop and raises `ApplicationStarted`. `host.StopAsync()` in
  `finally` raises `ApplicationStopping`, stops the hosted services in reverse
  order and raises `ApplicationStopped` before the event loop ends.
  `using var host` disposes the services after `Run` returned, as `Run` requires.
- **`StopApplication`.** Nothing waits for `IHostApplicationLifetime.StopApplication()`
  in this setup, so the registration closes the Window when a service calls it;
  `WaitForCloseAsync` then returns and the application stops as above.

`RunicCsWebUiHost.Run(host.Services, async runic => ...)` takes the same callback.
`RunicDesktopHost.Run` logs through the `ILoggerFactory` that Generic Host
registers, until it returns.
`GenericHostChecks` in
[Runic.Application.Views.Desktop.Tests](../tests/dotnet/Runic.Application.Views.Desktop.Tests/GenericHostChecks.cs)
runs this setup.

## Shipping appsettings.json

Copy the files next to the application. Items with `CopyToOutputDirectory` are
also published, so the packages of `dotnet runic package` contain them:

```xml
<ItemGroup>
  <None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Generic Host reads `appsettings.{Environment}.json` after `appsettings.json`.
The environment comes from `DOTNET_ENVIRONMENT` and is `Production` when it is
not set; `dotnet runic dev` sets `Development` unless you chose one. Environment
variables and the command line override both files.

To keep the defaults inside the executable, for example with a single-file
publish, embed the file and add it before the builder adds its own sources, so
files, environment variables and the command line still override it:

```xml
<ItemGroup>
  <EmbeddedResource Include="appsettings.defaults.json" LogicalName="appsettings.defaults.json" />
</ItemGroup>
```

```csharp
using (var defaults = typeof(Program).Assembly.GetManifestResourceStream("appsettings.defaults.json")!)
    settings.Configuration.AddJsonStream(defaults);
var builder = Host.CreateApplicationBuilder(settings);
```

`ConfigurationManager` reads a source when it is added, so the stream can be
disposed right after.

## Trimming and NativeAOT

`Configure<T>(section)`, `Bind` and `Get<T>` use reflection. The configuration
binding source generator replaces those calls with generated code that trimming
and NativeAOT keep. The SDK enables it when `PublishAot` or `PublishTrimmed` is
`true`; set it in the project file so that every build binds the same way and
shows its warnings:

```xml
<PropertyGroup>
  <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
</PropertyGroup>
```

## Per-user settings

Write what the user changes, such as preferences, recent files and window
placement, to a file under the user's profile, in a folder named after the
application ID (`ApplicationId` in the project file). Never write into the
application's directory: it is read-only in an MSIX package, a Flatpak, an
AppImage and a deb or rpm install, is shared by every user of the machine, and a
change inside a signed macOS bundle breaks its signature.

| | `SpecialFolder.ApplicationData` | `SpecialFolder.LocalApplicationData` |
| --- | --- | --- |
| Windows | `%APPDATA%` (roams with the user's profile) | `%LOCALAPPDATA%` (this machine) |
| macOS | `~/Library/Application Support` | `~/Library/Application Support` |
| Linux | `$XDG_CONFIG_HOME`, else `~/.config` | `$XDG_DATA_HOME`, else `~/.local/share` |

Use `ApplicationData` for preferences and `LocalApplicationData` for state that
belongs to one machine, such as window placement and caches.

- **Flatpak** sets `XDG_CONFIG_HOME` and `XDG_DATA_HOME` to
  `~/.var/app/<id>/config` and `~/.var/app/<id>/data`, which .NET follows.
- **Snap** sets `HOME` to `~/snap/<name>/<revision>`, so the folders are under
  it; snapd copies them to the next revision on refresh.
- **MSIX**: on Windows 10 version 1903 and later, a virtualized (packaged)
  application's new files under `AppData` go to a private per-package location
  that the application still sees at the usual path. Other programs do not see
  them there, and Windows removes them when the package is uninstalled.

Write the file atomically, so a crash or a full disk never leaves a half-written
file: write a temporary file in the same folder, then replace the old file with
it.

```csharp
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
    ApplicationIdentity.Id);
string path = Path.Combine(folder, "settings.json");
Directory.CreateDirectory(folder);
string temporary = path + ".tmp";
await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
{
    await JsonSerializer.SerializeAsync(file, userSettings, AppJsonContext.Default.UserSettings);
    file.Flush(flushToDisk: true);
}
File.Move(temporary, path, overwrite: true);
```

`File.Move` with `overwrite: true` replaces the file in one step on the same
volume. Read the file with a fallback to defaults when it is missing or cannot be
parsed, and serialize with a `JsonSerializerContext` for trimming and NativeAOT.
`ApplicationIdentity.Id` is the `runic-app` template's copy of `ApplicationId`.

## Window placement

Runic Desktop restores a Window's size, position, maximized and full-screen
state from a `DesktopWindowPlacement` that the application saves with its
per-user state: `RunicDesktopHostOptions.Placement` supplies it when a Window
opens, and `DesktopPlatformServices.GetWindowState(window)` reads it after the
Window closed. See
[`RunicDesktopHost.Placement`](../packages/dotnet/Runic.Application.Views.Desktop/README.md#entry-points)
and [Window state and restore](../packages/dotnet/Runic.Desktop/README.md#window-state-and-restore).
`DesktopWindowPlacement` is a record that `System.Text.Json` serializes as is.
On Linux the position is restored on X11, with GTK 3 and GTK 4; on Wayland the
compositor places every window, so a Window restores its size, maximized and
full-screen state but not its position, and the saved placement has none.
