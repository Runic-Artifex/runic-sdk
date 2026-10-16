# Runic.Application.Views.Desktop

Present Runic Windows and Views with
[Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md):
native windows, embedded WebViews or browser presentations, and the Runic
platform services.

```sh
dotnet add package Runic.Application.Views.Desktop --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

This is the default host of the `runic-app` project template: `dotnet new runic-app`
opens a native window with this host.

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
// Windows uses WebView2 and macOS WKWebView; Linux selects a toolkit explicitly.
var options = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };

return RunicDesktopHost.Run(provider, options, async host =>
{
    // Stops at startup when a registration, WebView runtime or window option is missing.
    host.ValidateWindow<MainWindow>().ThrowIfInvalid();
    await using var window = await host.OpenWindowAsync<MainWindow>(new RunicWindowOptions { Width = 800, Height = 600 });
    await window.WaitForCloseAsync();
    return 0;
});

public sealed partial class MainWindow : RunicWindow<MainViewModel>;
```

The same Window and `async host => ...` body run on CS-WebUI with
`RunicCsWebUiHost.Run(provider, ...)` from `Runic.Application.Views.CsWebUi`.

## Entry points

- `RunicDesktopHost.Run(provider, options, application)` starts a
  `DesktopHost` and runs the asynchronous application on the event loop the
  platform needs (`DesktopEventLoop`): the process main thread on macOS and with
  GTK 4. Call it from `Main` before any `await` and return its result. Ctrl+C
  and SIGTERM close the open windows, so `WaitForCloseAsync` returns, and cancel
  `host.Desktop.ShutdownRequested`. A body without a window, such as one behind
  a status icon, sets `DesktopHostOptions.HandlesShutdownWithoutWindow` and ends
  its loop on that token; without that option, a signal that arrives while no
  Window is open or served ends the process with its default action.
- `host.OpenWindowAsync<TWindow>(options, cancellationToken)`, from
  `Runic.Application.Views`, creates the Window's DI scope, resolves its
  ViewModel, opens a `DesktopSurface` and its presentation, and attaches the
  generated Bridge. The surface serves `RunicWindowOptions.ContentRoot` (`www`
  by default) and opens `StartPage`; `Width` and `Height` replace the window
  size. For other surface or window options, create
  `new RunicDesktopHost(provider, desktop) { SurfaceOptions = ..., WindowOptions = ... }`
  inside `DesktopEventLoop.Run`. `RunicDesktopHost.DefaultWindowOptions` opens
  the embedded WebView and falls back to an installed browser.
- `host.ValidateWindow<TWindow>(options)` checks, before any window opens, that
  the generated Bridge for the ViewModel is registered and that
  `DesktopHost.Validate` accepts the window options. It returns every problem
  as a `RunicWindowDiagnostic` with a stable code and remediation: an error
  `bridge-not-registered`, the
  [Runic Desktop codes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md),
  and a warning `viewmodel-not-registered` when the container does not report
  the ViewModel through `IServiceProviderIsService`. A container can resolve
  services it does not report, so only the Bridge check fails; a container
  without `IServiceProviderIsService` is not checked. `ThrowIfInvalid()` throws
  a `RunicWindowConfigurationException`. `OpenWindowAsync` runs the same checks
  before it creates a surface and throws the same exception; a
  `DesktopConfigurationException` from Runic Desktop becomes its
  `InnerException`. A missing Bridge is logged as event 1050 through the
  provider's `ILoggerFactory`.
- `DesktopBridgeWindow<TViewModel>`, the Window's `Host`, owns the Window's scope, surface, and attachment.
  It exposes `ViewModel`, `Surface`, `Presentation`, `WaitForCloseAsync`, and `CloseAsync`, which
  stops new operations and waits for accepted ones before releasing the scope.
  It implements `IBridgeWindow`, the lifetime contract shared with the CS-WebUI
  host, and returns the same `BridgeWindowCloseResult`.
- A Window with `GuardClose` (see
  [Guarding a user close](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#guarding-a-user-close))
  passes its `CanCloseAsync` to the embedded window as
  `DesktopWindowOptions.ConfirmCloseAsync`, ahead of a `ConfirmCloseAsync` of
  `WindowOptions`, so the title-bar close button, Alt+F4 or the window manager
  asks the navigation regions' departure guards. `window.RequestCloseAsync()`
  is the same native close request and shares its decision;
  `CloseAsync`, disposal, Ctrl+C and SIGTERM do not ask. An explicitly selected
  browser, the `EmbeddedThenBrowser` fallback, or a custom window host without
  the `CloseConfirmation` capability opens the Window unguarded for the user's
  close and logs event 1053 `WindowCloseGuardUnavailable`; `RequestCloseAsync`
  still asks `CanCloseAsync` there.
- `services.AddRunicPlatformServices()` registers native file dialogs, the
  text clipboard, and the file launcher for each window scope:
  `IWindowPlatformServices`, `IFileDialogs`, `ITextClipboard`, and
  `IDesktopFileLauncher`. A ViewModel takes them in its constructor before its
  window opens. They bind to the window's native owner when it opens, and the
  provider is selected from the backend that is actually running:
  - Windows: `Runic.Platform.Windows`.
  - macOS: `Runic.Platform.MacOS`.
  - Linux with GTK 3: `Runic.Platform.Linux`.
  - Linux with GTK 4 (`WithGtk4()`): xdg-desktop-portal dialogs and the file
    launcher through `Runic.Platform.Linux.Portal`, parented with
    `Runic.Platform.Linux.Gtk4`, plus the GTK 4 clipboard. A GTK 4 process
    never loads the GTK 3 provider.

  Until the window opens, every operation returns
  `Unavailable(OwnerUnavailable)`. It returns the same result when the window
  has no native owner, for example an installed browser after an
  embedded-window fallback. After the window closes, every operation returns
  `Unavailable(OwnerClosed)`. A Linux process without a GTK backend returns
  `Unavailable(ProviderNotConfigured)`. `GetSnapshot()` reports the same states
  per capability. The package references every provider package, but loads
  only the selected provider assembly. An application that targets
  `net10.0-windows`, such as a WPF application, gets an assembly that
  references only `Runic.Platform.Windows`, so it does not ship the Linux and
  macOS providers. `dotnet runic doctor` names the
  provider and, on Linux, checks for the session bus and xdg-desktop-portal.
  `PlatformServices.ForWindow(owner)` creates the same services for a
  `DesktopWindow` that the application opened itself.

  ```csharp
  services.AddScoped<NotesViewModel>();
  services.AddRunicViews();
  services.AddRunicPlatformServices();

  public sealed partial class NotesViewModel(IFileDialogs files) : ObservableObject
  {
      [ObservableProperty] private string _text = "";

      public async Task OpenAsync()
      {
          if (await files.OpenFileAsync(new()) is not PickerResult<IReadFileLease>.Selected(var lease)) return;
          await using (lease)
          await using (var stream = await lease.OpenReadAsync())
          using (var reader = new StreamReader(stream))
              Text = await reader.ReadToEndAsync();
      }

      // Replaces the file atomically, or leaves it unchanged.
      public async Task<bool> SaveAsync()
      {
          if (await files.SaveFileAsync(new("notes.txt")) is not PickerResult<ISaveFileLease>.Selected(var lease)) return false;
          await using (lease)
          {
              if (await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)
                  is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) return false;
              await using (transaction)
              {
                  await using (var writer = new StreamWriter(transaction.Content, leaveOpen: true))
                      await writer.WriteAsync(Text);
                  return await transaction.CommitAsync() is FileCommitResult.Committed;
              }
          }
      }
  }
  ```

  On Windows, Linux and for unsandboxed macOS selections, the save writes a staged
  sibling file and replaces the selection in one step. On Linux this also works
  inside Flatpak: the xdg-desktop-portal document view stages the sibling as a
  private host file and renames it onto the chosen file. A sandboxed macOS
  selection cannot stage a sibling yet, so there `BeginWriteAsync` returns
  `Unavailable(AtomicReplaceUnavailable)` before it changes the file. Linux can
  return the same result for a read-only or revoked grant, and `CommitAsync`
  returns `NotCommitted(Conflict)` when the file changed or appeared since
  selection. A `false` result therefore means the file was not changed: tell the
  user and offer Save As to another destination. Runic never falls back to
  overwriting the file in place. The
  [portal guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.Linux.Portal/README.md)
  describes the document-view mechanism and the GTK 3 opt-in. CS-WebUI's
  `AddRunicPlatformServices()` registers the same services, which return
  `OwnerUnavailable`.
- `AddRunicPlatformServices()` also registers `IDesktopSettings`, the
  desktop's appearance preferences (color scheme, accent color, high contrast,
  reduced motion). They belong to the application, not to a window, so there
  is one singleton, which `PlatformServices.CreateSettings()` creates when it
  is first resolved and the service provider disposes. Windows reads its
  settings, macOS reads AppKit, and Linux reads xdg-desktop-portal with either
  GTK version, without loading GTK. On other systems every read returns
  `Unavailable(ProviderNotConfigured)`, and so does CS-WebUI's registration. A
  ViewModel or service takes it in its constructor and watches it off the
  model context:

  ```csharp
  await foreach (var result in settings.WatchAsync(cancellationToken))
      if (result is PlatformResult<DesktopAppearance>.Success { Value: var appearance })
          await context.InvokeAsync(() => DesktopScheme = appearance.ColorScheme.ToString(), cancellationToken);
  ```

  The [multi-window example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/multi-window)
  stores the app's own theme in a settings file and follows this preference
  when the theme is "system". Register your own `IDesktopSettings` before
  calling `AddRunicPlatformServices()`, for example in tests, to replace it.
- `AddRunicPlatformServices()` also registers `IDesktopNotifications`, one
  singleton for the application, which `PlatformServices.CreateNotifications(applicationId)`
  creates when it is first resolved and the service provider disposes. A
  ViewModel takes it in its constructor, with no `OperatingSystem` branch:

  ```csharp
  services.AddRunicPlatformServices("org.example.Notes");

  public sealed partial class NotesViewModel(IFileDialogs files, IDesktopNotifications notifications) : ObservableObject
  {
      private async Task SavedAsync(string name) =>
          await notifications.ShowAsync(new DesktopNotification("saved", "Notes", $"Saved {name}."));
  }
  ```

  The application ID is the one string you pass: on Windows the
  AppUserModelID your installer registers with the shell, on Linux the
  reverse-DNS ID of the installed desktop entry (`org.example.Notes` for
  `org.example.Notes.desktop`); macOS uses the bundle identifier. Runic does
  not derive it from the assembly or the GTK application ID. The overload
  checks it for the running system and throws `ArgumentException` when it is
  invalid there. Without an ID, `AddRunicPlatformServices()` lets the Linux
  portal infer the identity from the desktop session, which suits
  development, and Windows requests return `Unavailable(ProviderNotConfigured)`;
  so does CS-WebUI's registration. Creating the service sends nothing and
  opens no connection; the first request does, and success acknowledges
  submission, not that the desktop showed it. On Linux, a rejected desktop ID
  is reported as `portal-identity-registration-failed`, and portal diagnostics
  are logged as event 2003 through the provider's `ILoggerFactory`. Register
  your own `IDesktopNotifications` first to replace it, for example a fake in
  tests.
- `DesktopBridgeWindow<TViewModel>.NativeOwner` (a `DesktopNativeOwner`) is the
  presentation's `INativePickerOwner`, which the services above bind to. The
  provider APIs that take it directly, such as
  `WindowsPlatformProvider.CreateFileDialogs(owner)` or
  `PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(owner))`,
  keep working for applications that compose providers themselves.
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

The package's build targets copy `runic-desktop-views.js` into `www/`, also as
`runic-views.js`. Load `webui.js` (served by the surface) and `runic-views.js`
before the generated client in `index.html`; the same page then runs on
CS-WebUI, whose package copies its own client as `runic-views.js`. A project
that references both hosts sets `RunicApplicationHost` (`desktop` or
`cswebui`); see
[the host of a page](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#the-host-of-a-page).
Pages that load `runic-desktop-views.js` keep working. `runic({ desktop: true })`
is not needed for a Views page. The adapter uses the same host-neutral client
contract as the CS-WebUI adapter, and its build defaults match
`Runic.Application.Views.CsWebUi`, so `AddRunicViews()` is generated in
`<ProjectName>.RunicBridgeComposition`.

The [First Window on Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window-desktop)
example is a complete application. See the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/)
to choose between this host and CS-WebUI.

## Automation

Set `RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` to start a template-shaped
application without a user. With it set, `RunicDesktopHost` closes every
window it opens, as soon as the presentation has opened and, with
`DesktopHostOptions.WaitForConnection`, its bridge has connected. Before
closing, it writes:

- a warning to standard error, and event 2002 through a registered
  `ILoggerFactory`, so an inherited setting is visible;
- `RUNIC_APPLICATION_OPENED=<presentation>` (for example `Embedded`, or the
  browser it fell back to) to standard output.

An application shaped like the `runic-app` template then sees `WaitForCloseAsync`
return and exits normally. Applications that keep running after a window
closes, or that open further windows, are not meant to use it. The template
acceptance check uses it to run the GTK 4 template under Xvfb.

Under `dotnet runic dev`, a full page reload, such as Vite's `full-reload`
after an edit, closes the View's connection and fails a script in flight
(`IOException`, then `No authenticated WebUI browser is connected` until the
page reconnects), so a test waits for the View, for example for an element it
renders, instead of assuming that a call lands; `@runic-artifex/vite-plugin-runic`
pre-bundles its development client, so a cold Vite dependency cache no longer
reloads the first page.

Set `RUNIC_APPLICATION_SERVE_ONLY=1` to drive the unchanged application from a
browser test, with the same contract as on
[CS-WebUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.CsWebUi/README.md).
`RunicDesktopHost` then opens no window or browser. Each window creates its
surface, writes `RUNIC_APPLICATION_URL=<url>` to standard output, and serves
until a line is read from standard input, a termination signal requests
shutdown, or the process ends; then `WaitForCloseAsync` returns. The URL is
the surface's capability URL, and the surface admits one browser. It takes
precedence over `RUNIC_APPLICATION_CLOSE_AFTER_OPEN`. As on CS-WebUI there is
no native owner: platform services report `OwnerUnavailable`, and a Window
with `GuardClose` opens unguarded (event 1053). `NativeOwner` throws, so bring
a Window forward with `PlatformServices.PresentAsync(window, context)`, which
takes the `IRunicWindow` and reports `OwnerUnavailable` in this mode and for a
Window on another host. `ValidateWindow` makes the same
decision: in serve-only mode it skips the presentation checks, such as
`webkitgtk-runtime-missing`, `graphical-session-missing` and
`presentation-unavailable`, which describe what this machine lacks to present a
window, so an application that validates at startup also starts headless. It
still reports configuration errors, such as `window-option-invalid`,
`browser-unsupported` or `gtk4-provider-missing`, which fail every launch that
presents a window.

The serve-only protocol, the same on both hosts:

- **URL.** Each window writes one `RUNIC_APPLICATION_URL=<url>` line to
  standard output once its surface serves. The first line is the first
  window's.
- **Stop.** Writing a line to standard input ends serving for every window
  serving at that moment; end of input, such as `</dev/null`, does not. On
  Runic Desktop a window the application opens after that line serves until
  the next one; on CS-WebUI the line ends the application. On Runic Desktop on Linux and
  macOS, SIGTERM or Ctrl+C also requests shutdown. On Windows a test cannot
  send another process a termination signal, and killing it skips the
  application's cleanup, so write the line.
- **Timeouts.** Bound both waits, and kill the process tree when the
  application does not exit after the line.

`RunicServeOnlyApplication` in
[Runic.Application.Testing](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md#the-whole-application-in-a-browser)
implements it for browser tests. A headless Chromium, as in the
[First Window on Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window-desktop)
browser smoke, does not exercise the embedded WebView engine.

To drive the embedded View itself from an in-process test, run script in it
through the window's surface and click the View's own controls, such as a
dialog's button:

```csharp
await using var window = await host.OpenWindowAsync<MainWindow>();
var surface = ((DesktopBridgeWindow<MainViewModel>)window.Host).Surface;
var title = await surface.ExecuteJavaScriptAsync(
    "return document.querySelector('h1')?.textContent ?? ''", TimeSpan.FromSeconds(10));
```

`ExecuteJavaScriptAsync` needs a connected page; with
`DesktopHostOptions.WaitForConnection` the window has connected when
`OpenWindowAsync` returns.
