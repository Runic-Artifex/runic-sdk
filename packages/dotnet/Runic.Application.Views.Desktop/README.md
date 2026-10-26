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
  Window is open or served ends the process with its default action. `provider`
  is any `IServiceProvider`, such as Generic Host's `host.Services`; the
  [configuration guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/configuration.md)
  shows how to start hosted services and read `appsettings.json` with this host.
- `host.OpenWindowAsync<TWindow>(options, cancellationToken)`, from
  `Runic.Application.Views`, creates the Window's DI scope, resolves its
  ViewModel, opens a `DesktopSurface` and its presentation, and attaches the
  generated Bridge. The surface serves `RunicWindowOptions.ContentRoot` (`www`
  by default) and opens `StartPage`; `Width` and `Height` replace the window
  size. For other surface or window options, pass a `RunicDesktopHostOptions`
  to `RunicDesktopHost.Run(provider, desktopOptions, hostOptions, async host => ...)`,
  or to `new RunicDesktopHost(provider, desktop, hostOptions)` for a host on a
  `DesktopHost` you started yourself. `RunicDesktopHost.DefaultWindowOptions` opens
  the embedded WebView and falls back to an installed browser. A build with
  `RunicApplicationFrontendDelivery=Embedded` embeds the frontend in the
  assembly, and the host serves that archive in place of `www/`; see
  [Ship the frontend inside the executable](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#ship-the-frontend-inside-the-executable).
  `RunicDesktopHostOptions.Assets` serves another `IAssetSource`, such as an
  archive read with `AssetArchive.ReadAsync`. `SurfaceOptions`, a Window's
  `ContentRoot` and `dotnet runic dev`'s live frontend take precedence.
- `RunicDesktopHostOptions.ContentSecurityPolicy` sends a `Content-Security-Policy`
  with the HTML of every Window's surface, unless `SurfaceOptions` set one of
  their own (`DesktopSurfaceOptions.ContentSecurityPolicy`). It is a header of
  its own next to the surface's `frame-ancestors` policy, and the WebView
  enforces both. The Bridge needs `'self'` scripts and connections and no
  `'unsafe-eval'`: `DesktopBridgeTransport` sends state as data, which the
  served bridge script parses with `JSON.parse` before it calls the View, so
  `default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'`
  works and blocks inline, foreign and evaluated scripts. Only
  `DesktopSurface.ExecuteJavaScriptAsync`, which evaluates a string in the
  page, needs `'unsafe-eval'`.
- `RunicDesktopHostOptions.Resources` serves application files that are not part of
  the frontend build, such as icons from a cache, from each Window's own
  address. It is called with each Window as it opens, after its ViewModel is
  set, and returns that Window's `ContentHandler` or `null`. The surface asks
  the handler only for a request its content has no file for, before 404, so
  the page loads `<img src="icons/org.example.App.png">` alike in a native
  window, in a browser after a fallback and under `RUNIC_APPLICATION_SERVE_ONLY`.
  The request path is decoded and can contain `..`: map it to known files
  rather than to the file system.

  ```csharp
  var hostOptions = new RunicDesktopHostOptions
  {
      Resources = window => window is DashboardWindow ? icons.ServeAsync : null,
  };
  ```
- `host.OpenWindowAsync<TWindow>(options, activation, cancellationToken)` on
  `RunicDesktopHost` opens a Window for a launch or a click: it opens the Window
  as above and presents it with the desktop's activation token or startup ID,
  such as `ApplicationLaunch.PlatformContext` of a launch the running instance
  received. Wayland compositors such as KWin give a new window focus only with
  that token. A Window that is already open is brought forward with
  `DesktopPlatformServices.PresentAsync(window, context)` instead.
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
  It exposes `ViewModel`, `Surface`, `Window`, `WaitForCloseAsync`, and `CloseAsync`, which
  stops new operations and waits for accepted ones before releasing the scope.
  `RunicDesktopHost.GetSurface(window)` returns the `Surface` of any Window of
  this host without naming its ViewModel type, and `null` for another host's;
  `DesktopPlatformServices.GetDesktopWindow(window)` returns its `DesktopWindow` the
  same way, also `null` under `RUNIC_APPLICATION_SERVE_ONLY=1`, for example to
  hide it with `HideAsync` or subscribe to `Closed`.
  It implements `IBridgeWindow`, the lifetime contract shared with the CS-WebUI
  host, and returns the same `RunicWindowCloseResult`.
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
- `DesktopPlatformServices.AnnounceAsync(window, message, priority)` asks the screen
  reader to speak a status message for a Window, such as "Saved", through its
  embedded window (`DesktopWindow.AnnounceAsync`; see
  [Accessibility](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#accessibility)).
  WebKitGTK reports no ARIA live region to Orca, so on Linux this is how a
  page's status reaches a screen reader. A ViewModel takes
  `IWindowPlatformServices` and calls its `AnnounceAsync(message, priority)`,
  which works on every host, or takes `DesktopWindowPlatformServices` and calls
  its `AnnounceAsync(message, priority)` with a `DesktopAnnouncementPriority`.
  All return `Success`, or `Unavailable` with `OwnerUnavailable` for a Window
  without an embedded window (an installed browser, serve-only mode, another
  host, or a ViewModel's window that has not opened yet), `OwnerClosed` once it
  closed, and `BackendUnavailable` for a window host without announcements,
  such as GTK 4 before 4.14.
- `RunicDesktopHost.Placement` restores each Window's size, position and
  maximized state from the last time it closed, and
  `DesktopPlatformServices.GetWindowState(window)` reads them to save. The callback is
  called with each Window as it opens, after its ViewModel is set, and returns
  the saved `DesktopWindowPlacement` or `null`; it becomes
  `DesktopWindowOptions.Placement`, which replaces the Window's size and keeps it
  on a monitor that exists now (see
  [Window state and restore](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#window-state-and-restore)).
  `GetWindowState` returns the embedded window's `DesktopWindowState`, also after
  the window closed and until the Window is disposed, so save it after
  `WaitForCloseAsync` returns:

  ```csharp
  return RunicDesktopHost.Run(provider, new DesktopHostOptions(), new RunicDesktopHostOptions
  {
      Placement = window => window is MainWindow ? settings.MainWindowPlacement : null,
  }, async host =>
  {
      await using var window = await host.OpenWindowAsync<MainWindow>();
      await window.WaitForCloseAsync();
      if (DesktopPlatformServices.GetWindowState(window) is PlatformResult<DesktopWindowState>.Success { Value: var state })
          settings.MainWindowPlacement = state.Placement;
      return 0;
  });
  ```

  A Window without a native owner, one on CS-WebUI or another host, one served
  under `RUNIC_APPLICATION_SERVE_ONLY` or an installed browser after a fallback,
  reports `Unavailable` with `OwnerUnavailable`, as `PresentAsync` does; a custom
  window host that reports no state reports `BackendUnavailable`. For each change
  while the window is open, subscribe to `DesktopWindow.StateChanged` of the
  host's `DesktopBridgeWindow<TViewModel>.Window`. A placement belongs to the machine and operating
  system that saved it: its units are each platform's.
- `host.UseApplicationMenu(commands)` runs the application's menu bar commands
  by id until the returned object is disposed. They are application-wide, the
  `DesktopHost.MenuCommands` of every host on that Desktop host, so a second
  call before the first is disposed throws. A Window's root ViewModel that
  implements `IRunicMenuCommandSource`
  runs its own while the Window is key (see
  [Menu bar commands](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#menu-bar-commands)).
  Declare the menus in `DesktopHostOptions.MenuBar`; macOS and the GTK 3 and GTK 4
  windows show them. With `MenuBarPresentation = DesktopMenuBarPresentation.Page`
  in `WindowOptions` the page renders the bar with its own components through
  `createMenuBarController` in `@runic-artifex/views`; every Window serves its
  routes (see
  [In-page menu bar](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/web/views/README.md#in-page-menu-bar)),
  and a page's invoke runs through `DesktopWindow.TryExecuteMenuItemAsync`. Each
  item's `CommandParameter` is the command's parameter. A ViewModel that takes
  `IWindowPlatformServices` shows a native context menu with
  `ShowContextMenuAsync`, which `DesktopWindowPlatformServices` implements for the
  embedded window.
- A Window's root ViewModel that implements `IRunicFileDropTarget` receives the
  files the user drops onto the embedded window, with their native paths, in
  its model turn (see
  [Receiving dropped files](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#receiving-dropped-files)).
  A window served under `RUNIC_APPLICATION_SERVE_ONLY` or shown in an installed
  browser after a fallback receives none.
- `services.AddRunicPlatformServices()` registers native file dialogs, the
  clipboard, and the file launcher for each window scope:
  `IWindowPlatformServices`, `IFileDialogs`, `ITextClipboard`,
  `IDesktopClipboard` (HTML, RTF, images, files and a format watch), and
  `IDesktopFileLauncher`; `IWindowPlatformServices` also sets the window's taskbar
  badge and progress (Windows), the Dock tile's (macOS) or the application's Unity
  launcher entry (GTK 3 and GTK 4), which `AddRunicPlatformServices(applicationId)`
  names with the application ID (inside Flatpak the Flatpak ID serves). A ViewModel takes them in its constructor before its
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
  `DesktopPlatformServices.ForWindow(owner)` creates the same services for a
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
          if (await files.OpenFileAsync(new() { Filters = [new("Notes", ["txt", "md"])] }) is not PickerResult<IReadFileLease>.Selected(var lease)) return;
          await using (lease)
          await using (var stream = await lease.OpenReadAsync())
          using (var reader = new StreamReader(stream))
              Text = await reader.ReadToEndAsync();
      }

      // Replaces the file atomically, or leaves it unchanged.
      public async Task<bool> SaveAsync()
      {
          if (await files.SaveFileAsync(new("notes.txt") { Filters = [new("Notes", ["txt"])] }) is not PickerResult<ISaveFileLease>.Selected(var lease)) return false;
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

  Filters, the initial folder and `OpenFilesAsync` for several files work with
  every provider; the
  [Runic.Platform guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#file-dialog-options)
  lists what each platform does with them.

  On Windows, Linux and for unsandboxed macOS selections, the save writes a staged
  sibling file and replaces the selection in one step. On Linux this also works
  inside Flatpak: the xdg-desktop-portal document view stages the sibling as a
  private host file and renames it onto the chosen file. A sandboxed macOS
  selection stages in the volume's item replacement directory instead and
  `NSFileManager` replaces the chosen file. `BeginWriteAsync` returns
  `Unavailable(AtomicReplaceUnavailable)` on Linux for a read-only or revoked
  grant before it changes the file, and `CommitAsync`
  returns `NotCommitted(Conflict)` when the file changed or appeared since
  selection. A `false` result therefore means the file was not changed: tell the
  user and offer Save As to another destination. Runic never falls back to
  overwriting the file in place. The
  [portal guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.Linux.Portal/README.md)
  describes the document-view mechanism and the GTK 3 opt-in. CS-WebUI's
  `AddRunicPlatformServices()` registers the same services, which return
  `OwnerUnavailable`.

  An editor that saves back to the file it opened keeps a document lease from
  `OpenDocumentAsync` (or `SaveDocumentAsync` for Save As) and writes it on every
  save. A `Conflict` means another program changed the file; reading it again
  makes its content the one the next save compares with:

  ```csharp
  public sealed partial class EditorViewModel(IFileDialogs files) : ObservableObject
  {
      private IDocumentFileLease? _document;
      [ObservableProperty] private string _text = "";

      public async Task OpenAsync()
      {
          if (await files.OpenDocumentAsync(new() { Filters = [new("Notes", ["txt", "md"])] }) is not PickerResult<IDocumentFileLease>.Selected(var document)) return;
          if (_document is not null) await _document.DisposeAsync();
          _document = document;
          await ReloadAsync();
      }

      // Also after a conflict, when the user chooses the other program's version.
      public async Task ReloadAsync()
      {
          if (_document is null || await _document.OpenReadAsync() is not PlatformResult<Stream>.Success(var stream)) return;
          await using (stream)
          using (var reader = new StreamReader(stream))
              Text = await reader.ReadToEndAsync();
      }

      // Ctrl+S and autosave, as often as needed.
      public async Task<FileCommitResult?> SaveAsync()
      {
          if (_document is null || await _document.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)
              is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) return null;
          await using (transaction)
          {
              await using (var writer = new StreamWriter(transaction.Content, leaveOpen: true))
                  await writer.WriteAsync(Text);
              return await transaction.CommitAsync();
          }
      }
  }
  ```

  The lease belongs to the window and is released when it closes. Keep a
  recent-files entry with `CreateBookmarkAsync` and reopen it with
  `IDesktopFileBookmarks.OpenDocumentAsync`, which `AddRunicPlatformServices()`
  registers; the
  [Runic.Platform guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#document-apps)
  describes conflicts, bookmarks and what each platform supports.
- `AddRunicPlatformServices()` also registers `IDesktopSettings`, the
  desktop's appearance preferences (color scheme, accent color, high contrast,
  reduced motion). They belong to the application, not to a window, so there
  is one singleton, which `DesktopPlatformServices.CreateSettings()` creates when it
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
  singleton for the application, which `DesktopPlatformServices.CreateNotifications(applicationId)`
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
  AppUserModelID your installer registers with the shell (an MSIX package's
  process uses the package's AppUserModelID instead), on Linux the
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
- `AddRunicPlatformServices()` also registers `IDesktopStatusIcon`,
  `IDesktopLoginStartup` and `IDesktopInhibition`, one singleton each for the
  application, created by `DesktopPlatformServices.CreateStatusIcon(applicationId)`,
  `CreateLoginStartup(applicationId)` and `CreateInhibition(applicationId)`.
  Inhibition keeps the system or display awake while an operation holds a lease:

  ```csharp
  if (await inhibition.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Exporting documents")
      is PlatformResult<IDesktopInhibitionLease>.Success(var lease))
      await using (lease) await ExportAsync();
  ```

  Windows uses a power request and macOS an IOKit power assertion; Linux asks
  the xdg-desktop-portal Inhibit interface without a parent window, so the
  request belongs to the application, not to a window. The application services
  need no native owner and work the same in an installed browser after a
  fallback; the window services report `OwnerUnavailable` there. The
  [Runic.Platform guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#hosts-without-a-native-owner)
  lists every service's result without a native owner. Register
  `IDesktopPrivilegedActions` yourself; it needs declared actions.
- `AddRunicPlatformServices()` also registers `IDesktopVolumes`, one singleton
  created by `DesktopPlatformServices.CreateVolumes()`, which lists, mounts, unmounts
  and ejects storage volumes and watches them. Linux uses UDisks2 on the system
  bus with either toolkit; Windows uses the volume management API and ejects as
  "Safely Remove Hardware" does; macOS uses Disk Arbitration. See
  [Volumes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#volumes).
- `AddRunicPlatformServices()` also registers `IDesktopNetworkStatus`, one
  singleton created by `DesktopPlatformServices.CreateNetworkStatus()`: online,
  metered and captive portal, read or watched, from the Network List Manager's
  connectivity hint on Windows, Network.framework's path monitor on macOS and the
  NetworkMonitor portal or NetworkManager on Linux; see
  [Network status](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#network-status).
  Per window it registers `IDesktopMessageBoxes`, the window's native message
  boxes: task dialogs on Windows, also for a WPF window, sheets on macOS and GTK
  message or alert dialogs on Linux; see
  [Message boxes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#message-boxes).
- The secret store and the application database are registered without an
  application ID, also by `AddRunicPlatformServices(applicationId)`.
  `DesktopPlatformServices.CreateSecretStore(applicationId)` and
  `CreateApplications(applicationId)` create them for an ID, checked as the
  overload above checks it; on Linux their portal requests and diagnostics then
  carry it, and the application database's portal connection registers it as
  notifications do. Register one before calling `AddRunicPlatformServices` to
  use it. Secrets are stored the same way with or without an ID.
- `DesktopBridgeWindow<TViewModel>.NativeOwner` (a `DesktopNativeOwner`) is the
  presentation's `INativeWindowOwner`, which the services above bind to. The
  window services (file dialogs, the clipboard, file handoff and message boxes) are created only
  here, through `DesktopPlatformServices.ForWindow`; the providers keep them internal.
  Pass the owner where an API asks for a window, such as
  `WindowsElevationOptions.Owner` for the UAC prompt.

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
`<RootNamespace>.RunicViewsComposition`.

The [First Window on Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window-desktop)
example is a complete application. See the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/)
to choose between this host and CS-WebUI.

An exception from the application body passed to `Run` closes the windows,
disposes the host and is rethrown by `Run`; Runic does not log it. The
[crash reporting guide](https://docs.runic-artifex.eu/guides/desktop/crash-reporting/)
shows how to log such crashes, capture a dump and keep NativeAOT symbols.

## From a command line

An application with a command line, such as `my-app ui` next to other commands,
starts its Window from a
[Runic.CommandLine](https://www.nuget.org/packages/Runic.CommandLine) command
with Runic Command Line 0.7.0-preview.2 or later (see its guide,
[Start a UI host on the main thread](https://github.com/Runic-Artifex/runic-cli-sdk/blob/v0.7.0-preview.2/packages/dotnet/Runic.CommandLine/README.md#start-a-ui-host-on-the-main-thread)). `RunicDesktopHost.Run`
and `DesktopEventLoop.Run` must be called on the process main thread before any
`await`, and a command runs asynchronously, so the command does not start the
host itself: it hands the host's start back to `Main` with
`CommandExecutionContext.ContinueOnCallingThread`, and `Main` returns
`CommandApp.Run(args)` instead of awaiting `RunAsync`. `Run` blocks until the
command completed, then runs the continuation on the thread that called it and
returns its result as the exit code:

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Generated;

// A synchronous Main, or no await before this line: the thread is the process main thread.
return new CommandApp(GeneratedCommandCatalog.Create()) { Name = "my-app" }.Run(args);

internal static class WindowCommands
{
    [Command("ui", Description = "Open the main window.")]
    internal static async Task Ui(CommandExecutionContext context,
        [Option("--settings")] string? path = null, CancellationToken cancellationToken = default)
    {
        // Asynchronous work and its failures belong to the command, before the host starts.
        AppSettings settings = await AppSettings.LoadAsync(path, cancellationToken);
        context.ContinueOnCallingThread(() => DesktopEventLoop.Run(new DesktopHostOptions(), async desktop =>
        {
            await using var provider = new ServiceCollection()
                .AddSingleton(settings)
                .AddScoped<MainViewModel>()
                .AddRunicViews()
                .BuildServiceProvider();
            var host = new RunicDesktopHost(provider, desktop);
            await using var window = await host.OpenWindowAsync<MainWindow>();
            await window.WaitForCloseAsync();
            return 0;
        }));
    }
}
```

`DesktopEventLoop.Run` and `new RunicDesktopHost(provider, desktop)` let the
service provider be created and disposed asynchronously on the event loop.
`RunicDesktopHost.Run(provider, options, application)` works the same way in a
continuation for a provider built before it.

The continuation runs only when the command succeeded and was not cancelled,
after its output was written and its scope disposed, so capture values, such as
`settings`, rather than the command's scoped services. Help, usage errors and
the other commands never start the host, and a failed command returns its exit
code without opening a Window. Runic.CommandLine stops handling Ctrl+C before
the continuation starts; the Desktop host then handles Ctrl+C and SIGTERM as
described above. Under `CommandApp.RunAsync`, the hosting adapter's `RunAsync`
or a directly used `CommandExecutor`,
`CommandExecutionContext.CanContinueOnCallingThread` is `false` and
`ContinueOnCallingThread` throws.

Test such a command with `CommandAppTester.Run` from
`Runic.CommandLine.Testing`, which runs the continuation on the calling
thread, or start the application in serve-only mode with
`RunicServeOnlyApplication` from `Runic.Application.Testing`.

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

Without a Vite or Angular development server, `dotnet runic dev` sets
`RUNIC_APPLICATION_DEVELOPMENT_ASSETS` to the frontend output directory, such
as `Frontend/dist`, which the frontend watcher rewrites. In a `Development`
environment, and when `RunicDesktopHostOptions.SurfaceOptions` is not set,
each window then serves that directory before its `www/` folder (which still
supplies `runic-views.js`) and reloads when the directory changes: it navigates
to the last HTML page it loaded, or to its start page, without the page's
query, hash or `history.pushState` route, and its Bridge reconnects as after
any reload. A connected page is navigated through its Bridge; a window whose
page has no Bridge, such as a half-written `index.html` or a build error page,
is navigated through its WebView instead. Neither runs page script. Outside `Development` the host ignores the variable. Events 2005 to
2007 report it. The
[dotnet-runic README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)
describes the frontend modes.

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
a Window forward with `DesktopPlatformServices.PresentAsync(window, context)`, which
takes the `IRunicWindow` and reports `OwnerUnavailable` in this mode and for a
Window on another host. `ValidateWindow` makes the same
decision: in serve-only mode it skips the presentation checks, such as
`webkitgtk-runtime-missing`, `graphical-session-missing` and
`presentation-unavailable`, which describe what this machine lacks to present a
window, so an application that validates at startup also starts headless. With
a browser fallback, as in the default `EmbeddedThenBrowser` options, a missing
browser stands in for an installed one, so the embedded WebView's findings,
such as `linux-embedded-backend-not-selected`, are skipped too and no browser
is needed. It still reports configuration errors, such as `window-option-invalid`,
`browser-unsupported` or `gtk4-provider-missing`, which fail every launch that
presents a window.

A served Window can end with its last browser tab, as a `--browser` command
that opens the URL in the default browser would. The Window's surface counts
its connected pages in `ConnectionCount`, and a connection event's handler
already sees the count with that event. A reload disconnects before or after
the page connects again, so wait a moment before closing:

```csharp
var surface = RunicDesktopHost.GetSurface(window)!;
using var connections = surface.SubscribeConnectionEvents(connection =>
{
    if (connection is not { Kind: DesktopConnectionEventKind.Disconnected, Surface.ConnectionCount: 0 }) return;
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (surface.ConnectionCount == 0) await window.CloseAsync(TimeSpan.Zero);
    });
});
OpenInBrowser(surface.Url);
await window.WaitForCloseAsync();
```

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
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var title = await surface.ExecuteJavaScriptAsync(
    "return document.querySelector('h1')?.textContent ?? ''", timeout.Token);
```

`ExecuteJavaScriptAsync` needs a connected
page; with `DesktopHostOptions.WaitForConnection` the window has connected when
`OpenWindowAsync` returns. The token bounds the wait. A result larger than
16 MiB of UTF-8 throws a `DesktopException` with the category `LimitExceeded`
and the code `script-result-too-large`; it is never cut.
