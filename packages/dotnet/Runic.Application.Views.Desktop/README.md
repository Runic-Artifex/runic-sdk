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
  the embedded WebView and falls back to an installed browser; change one option
  of every Window with `with`, such as the native spell checker, which an
  application that brings its own turns off
  ([Spell checking](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#spell-checking)):

  ```csharp
  var hostOptions = new RunicDesktopHostOptions
  {
      WindowOptions = RunicDesktopHost.DefaultWindowOptions with { SpellChecking = false },
  };
  ```

  A build with
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
  `'unsafe-eval'`: `DesktopBridgeTransport` sends Views pushes as raw data to
  `__runicViewsPush`, which the served bridge script hands to
  `runic-desktop-views.js` without evaluating them, so
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
  `RunicDesktopHost`, and since 0.14 on any `IRunicWindowHost` (CS-WebUI opens the
  Window and ignores the context), opens a Window for a launch or a click: it opens the Window
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
  browser, a browser fallback (`EmbeddedThenBrowser`, `Fallbacks`), or a custom window host without
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
- The window's locale (`IRunicWindowLocale`, set before the window opens) gives
  an embedded window on Windows and Linux its native direction
  (`DesktopWindowOptions.LayoutDirection`, right to left for `ar`, `he`, `fa`
  and other right-to-left scripts) and the web view's language
  (`DesktopWindowOptions.Language`: built-in text such as the context menu and
  form validation, `navigator.language`). A later locale change, including one
  while the window opens, mirrors the open window or returns it to left to right
  (`DesktopWindow.SetLayoutDirectionAsync`), and a `null` locale returns it to the
  platform's direction. Changes apply in order, so the window ends with the
  direction of the last one. The script of a tag counts only right after its
  language: `ar-u-nu-latn` (Arabic with Latin digits) stays right to left. The
  web view's language stays until the window opens again. A
  `LayoutDirection` or `Language` that `WindowOptions` sets wins, and a
  `ProfilePath` keeps the platform's language, since WebView2 needs one language
  per profile. macOS takes both from the application bundle. The page sets its
  own `<html lang dir>` (`syncDocumentLocale` in `@runic-artifex/views`); the
  pages the window serves itself, such as Resource Not Available, take `lang`
  and `dir` from the window locale and their text from `DesktopPlatformTexts`.
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
  `createMenuBarController` in `@runic-artifex/views`; every Window serves the
  reserved route `runic.menu-bar` on its Views connection, with
  [menu bar page protocol 2](../../../specs/desktop/menu-bar-page.md) (see
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
  embedded-window fallback, except for file dialogs and message boxes that ask
  for `OwnerPolicy.PreferOwner`, which show without an owner, and the file
  launcher, which is the application's. After the window closes, every operation returns
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
  desktop's appearance preferences (color scheme, accent color, contrast and
  forced colours, reduced motion, text scale). They belong to the application, not to a window, so there
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
  `RunicDesktopHost` watches the same `IDesktopSettings` (or, without the
  registration, the platform's own) while any Window is open, and pushes the
  appearance to each Window's pages (`appearance/1`,
  [Appearance](../Runic.Application.Views/README.md#appearance)), so the page's
  `useAppearance` sees the desktop's text scale, accent colour, contrast and
  forced-colours palette. A serve-only Window, and every Window of a host with
  `DesktopHostOptions.NetworkExposure = AllInterfaces`, gets none: their pages
  may run on another machine.
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
- `AddRunicPlatformServices()` also registers `IDesktopPowerEvents`, one singleton
  created by `DesktopPlatformServices.CreatePowerEvents()`: sleep, wake, session
  lock and battery events from `WM_POWERBROADCAST` and WTS on Windows, `NSWorkspace`
  and IOKit on macOS, and logind and UPower on Linux. It decides on first use: once a
  `RunicDesktopHost` with `ServeOnly` was built on the provider, or under
  `RUNIC_APPLICATION_SERVE_ONLY=1`, it reports `Unavailable` and opens no watches; see
  [Power events](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#power-events).
- `AddRunicPlatformServices()` also registers `IDesktopRecentDocuments`, the
  application's recent documents (`DesktopPlatformServices.CreateRecentDocuments()`):
  Open Recent and the Dock on macOS, Recent items and the Jump List on Windows,
  and `recently-used.xbel` on Linux, the same lists as
  `DesktopHost.NoteRecentDocumentAsync`, with each outcome as a `PlatformResult`.
  It decides on first use: once a `RunicDesktopHost` with `ServeOnly` was built on
  the provider, or under `RUNIC_APPLICATION_SERVE_ONLY=1`, it reports `Unavailable`.
  With `AddRunicPlatformServices(applicationId)` Linux names the application by its
  ID; see
  [Document apps](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#document-apps).
- `AddRunicPlatformServices()` also registers `IApplicationPlatformServices`, the
  one instance of `DesktopPlatformServices.ForApplication()`: the application's
  `FileLauncher` opens URIs and files and reveals files without a window, also
  before the first window opens, behind a status icon and for Windows served
  under serve-only mode, and its `MessageBoxes` show boxes that ask for
  `OwnerPolicy.PreferOwner` without a window, such as a fatal start-up error. Windows hands them to the shell on an STA thread of the
  application's own, Linux asks the desktop portal without a parent window
  (either toolkit or none) and macOS asks NSWorkspace on the main thread. A
  window shown in an installed browser after a fallback uses it as its own
  `FileLauncher`, until the window closes. Code that runs before the container
  exists calls `ForApplication()` directly.

  ```csharp
  public sealed class HelpLinks(IApplicationPlatformServices platform)
  {
      public ValueTask<PlatformResult<PlatformUnit>> OpenLogFolderAsync(string log) =>
          platform.FileLauncher.LaunchAsync(log, DesktopFileOperation.Reveal);
  }
  ```
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
  themselves. It is an `IBridgeConnectionTransport`: `Serve` delivers the
  Views protocol 4 requests that the page sends on the one capability
  `__runicViews`, `BroadcastAsync` pushes to every page of the surface and
  `SendAsync` to one connection, both as raw data to `__runicViewsPush`. A
  connection is one authenticated WebSocket; its Desktop connection id is
  both its connection and its client key, so a reconnect is a new connection.
  A Window's `IRunicConnections` (since 0.16) therefore lists the page's one
  connection, a new one after each reload, and its `Caller` is that connection
  during a page's command or in-page menu click, and `null` for the native menu
  bar and host shortcuts
  ([Who is connected, and who called](../Runic.Application.Views/README.md#who-is-connected-and-who-called)).
  The requests of one connection are admitted in the order the page sent
  them. Disposing the transport removes the capability and stops pushes; the
  surface stays open. The per-route `Bind`, `BindAsync`, `Publish`,
  `PublishAsync` and `PublishUtf8Async` of earlier releases are gone; see
  [Custom window hosts](../Runic.Application.Testing/README.md#custom-window-hosts)
  for the transport contract.

The surface owns authenticated browser or embedded WebView sessions and
removable capability bindings; the application Window owns its ViewModel scope
and logical View lifetimes. `DesktopHostOptions` and `DesktopWindowOptions`
select the browser or embedded backend; the Runic Desktop guide lists the
native prerequisites for each platform.

The package's build targets copy `runic-desktop-views.js` into `www/`, also as
`runic-views.js`. It installs `__runicBridge`: requests go to `__runicViews`,
and pushes arrive on the WebSocket as they are sent, so the script does not
poll. Load `webui.js` (served by the surface) and `runic-views.js`
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

## Hosted applications

A Runic Desktop application, the **host**, can show the Windows of another
Runic Desktop application, the **guest**, in native windows of its own. Typical
hosts are launchers, suite shells and IDE-like applications that run tools as
separate processes. Each guest keeps its own process, ViewModels, translations
and Runic version, so a guest that crashes takes only its own windows with it.
The two talk over the
[Runic host protocol](../../../specs/desktop/host-protocol.md), `runic-host/1`.
The [hosted windows example](../../../examples/hosted-windows/README.md) is a
shell that hosts a counter.

**Experimental.** The host and guest APIs, `RunicTestHost` and the protocol are
experimental: suppress
[`RUNICDESK103`](../../../docs/experimental.md#RUNICDESK103). They can change in
a minor release, also after 1.0.

**Quick start.** The guest opts in on a code path of its own. The host starts
the guest's executable with the parameter that selects that path:

```csharp
#pragma warning disable RUNICDESK103 // Hosted application windows are experimental.
// The guest (its Program.cs): only its own `hosted` parameter accepts a host.
if (args is ["hosted"])
    return RunicDesktopHost.Run(provider, new DesktopHostOptions(),
        new RunicDesktopHostOptions { Guest = RunicGuestOptions.FromStandardInput() },
        async host =>
        {
            await using var window = await host.OpenWindowAsync<MainWindow>();
            await window.WaitForCloseAsync();
            return 0;
        });

// The host: each StartAsync starts a guest process for this host session.
await using var tool = await host.HostedApplications.StartAsync(
    new RunicGuestStartInfo("/opt/tool/Tool", ["hosted"]),
    new RunicHostedApplicationOptions { Policy = new ToolPolicy() });
```

[Hosted mode](#hosted-mode) describes the guest side and
[Hosting another application](#hosting-another-application) the host side. Test
a guest without a host application with `RunicTestHost`
([a hosted application](../Runic.Application.Testing/README.md#a-hosted-application)).
An application changes behaviour only when it opts in. An application without a
guest code path cannot be hosted, and one that never calls `StartAsync` hosts
nothing.

**Opt-in and policy.** Runic defines no flag or environment variable for hosted
mode, so the processes a guest starts never inherit it. The host decides what a
guest may do with `IRunicHostedWindowPolicy`: which windows open and how, its
presentation requests, menus, dialogs and window services. Without a policy,
every window, menu and dialog shows unchanged. Window services follow
conservative defaults: the clipboard is read and written only for the window the
user works in, and only `http`, `https` and `mailto` links and files the user
gave the guest are opened. A policy can mark a guest's dialogs, refuse its message boxes or narrow
the launcher. The example's `CounterPolicy` does both.

**Limits.** `RunicHostedLimits`, from the policy's `Limits`, bounds a guest's
requests. Its defaults are 20 menu requests a second per window, 80 per guest,
one dialog at a time per guest, 10 dialogs per ten seconds, 20 clipboard and
monitor requests a second, 5 opened files or links per ten seconds, and 2
requests a second that bring a window to the front or announce text. The
protocol also bounds messages (1 MiB), waiting requests (64), menus (8 levels,
1,000 entries) and dialog texts
([limits](../../../specs/desktop/host-protocol.md#limits)).

**Security in short.** Both sides opt in. The channel is a loopback WebSocket
that only the host that started the guest can join: a nonce on the guest's
standard input and a key on its standard output prove it, and an upgrade from a
web page is refused. A guest's page gets none of the host's credentials or
cookies, and each guest session has its own web view data store. The guest
reaches its own windows only. Elevated guests, and guests of another user than
their parent, refuse hosted mode. Any process of the same user that knows a
guest's parameter can host it; `RunicGuestOptions.AcceptHost` is a product
filter, not a security boundary. A guest words its own dialog and menu texts,
so a host that does not trust a guest marks or refuses them in its policy. The
[security guide](../../../docs/security.md#hosted-application-windows) has the
details.

**Platform notes.**

- Hosts and guests run on Runic Desktop only. A CS-WebUI, hosted or serve-only
  application cannot host.
- Hosted windows have no browser fallback: a browser tab would send the user's
  own `127.0.0.1` cookies to the guest. The host needs an embedded web view.
  WebView2 and WKWebView are built in. On Linux the host selects one:
  `DesktopHostOptions.Linux.EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41`,
  or `WithGtk4()` from `Runic.Desktop.Gtk4`.
- WebView2 gives each guest session an InPrivate profile in a user data folder
  of its own, deleted after the session, with a browser process of its own.
- On WKWebView and WebKitGTK, a guest session's data store lives only while one
  of its windows is open. A guest that opens a window after all its windows
  closed starts with empty cookies and storage.
- On macOS the one menu bar shows the host's application menu and then the
  guest's menus while a hosted window is key.
- On Windows a guest runs in a kill-on-close job object, which ends it also when
  the host crashes. On Linux and macOS it leads a process group, and a guest
  whose host crashed exits when its standard input ends.

### Hosted mode

The application opts in on a code path of its own, reached by a parameter it
defines, such as a hidden `hosted` command. That path runs
`RunicDesktopHost.Run` with `RunicDesktopHostOptions.Guest`:

```csharp
#pragma warning disable RUNICDESK103 // Hosted application windows are experimental.
if (args is ["hosted"])
{
    try
    {
        return RunicDesktopHost.Run(provider, new DesktopHostOptions(),
            new RunicDesktopHostOptions { Guest = RunicGuestOptions.FromStandardInput() },
            async host =>
            {
                // When the host goes away while the Window opens, the open throws; Run then returns 0.
                await using var window = await host.OpenWindowAsync<MainWindow>();
                await window.WaitForCloseAsync();
                return 0;
            });
    }
    catch (RunicHostedApplicationException error)
    {
        Console.Error.WriteLine($"Not hosted ({error.Code}): {error.Message}");
        return 1;
    }
}
```

- **Start.** Before the body runs, `Run` reads the host's offer from standard
  input, opens the host channel on a loopback listener of its own, writes its
  address and a random key to standard output and waits for the host to connect
  and prove that it started this process. A failure, such as no offer or a host
  that `RunicGuestOptions.AcceptHost` declines, throws
  `RunicHostedApplicationException` with its `Code` and logs event 2010. Hosted
  mode refuses to start elevated, with `DesktopHostOptions.NetworkExposure`
  other than loopback, or, on Linux and macOS, when its real or effective user
  is not its parent's or it cannot read its parent's (`guest-other-user`).
- **Windows.** No window toolkit loop starts, and the application never shows a
  window of its own. Each Window's page is served as in serve-only mode, and the
  host opens a window that loads it with a one-time launch address; the host's
  window policy may refuse it (`RunicWindowConfigurationException` with
  `hosted-window-refused`). The Window's `IRunicWindowPresentation` (present,
  minimize, visibility, full screen, document edited, colour scheme, activity)
  goes through the host. A user close of the host's window asks the Window's
  guard (`GuardClose`) first. A presentation request that the host has not
  answered within 30 seconds reports `Failed` with `Unknown`, and an open that
  the host has not answered by then fails with `hosted-window-failed`; the
  application's own event handlers for the presentation run off the channel, so
  a handler that waits cannot stall it. Recent documents
  (`IDesktopRecentDocuments`) report `BackendUnavailable`.
- **Menus and dialogs** (`menus/1`, `dialogs/1`, when the host lists them). The
  application's menu bar (`DesktopHostOptions.MenuBar`, `DesktopHost.SetMenuBar`)
  shows in each hosted window, with each item enabled and checked as the
  Window's command path decides: the root ViewModel (`IRunicMenuCommandSource`,
  `IRunicMenuCheckedSource`), then the application's commands
  (`UseApplicationMenu`). A new bar, such as one with titles in another
  language, and each change of an item's state reach the host; an item the user
  chooses in the host's bar, or whose shortcut they press, runs through that
  same path. With `services.AddRunicPlatformServices()`, the Window's
  `IWindowPlatformServices` shows its context menus, file dialogs (`IFileDialogs`,
  documents included) and message boxes (`IDesktopMessageBoxes`) as the host's,
  owned by the host's window: the application gets the same leases and results
  as from its own dialogs, opens the selected files itself, and one dialog at a
  time (another reports `ResourceBusy`). A dialog the host's policy refuses
  reports `PermissionDenied`; one whose window closes reports `OwnerClosed`;
  cancelling the call closes the host's dialog. Every menu or dialog reports
  `OwnerUnavailable`, as in serve-only mode, when the host does not list its
  feature.
- **Appearance and locale.** The Windows' pages show the host's appearance
  (colour scheme, contrast, forced colours and their palette, reduced motion,
  text scale, accent colour), never this process's own desktop settings: the
  windows show on the host's desktop. With `AddRunicWindowService`, the
  application's locale (`IRunicApplicationLocale`) follows the host's, already
  when the body starts, until the application sets one of its own with
  `SetLocaleAsync`; setting `null` follows the host's again. Each Window that
  follows the application follows it too, and the host's window takes its
  locale's direction. `RunicDesktopHost.Guest` has the host's `Locale`, its
  user's `Languages` (to negotiate against the application's catalogs instead
  of `DesktopLanguages.Preferred`) and its `Appearance`. A follow that fails is
  logged (event 2023).
- **Window services** (`window-services/1`, when the host lists it). The
  Window's clipboard (`IDesktopClipboard`: text, HTML, RTF, PNG images and file
  lists), monitors (`IDesktopMonitors`, with a watch that follows the host's
  changes) and file launcher (`IDesktopFileLauncher`) are the host's, and the
  files the user drops onto the host's window reach a root ViewModel that is an
  `IRunicFileDropTarget`. The host decides each request: by default it reads
  and writes the clipboard only for the window the user works in (not one the
  application presented or opened itself until the user switches to it, uses
  its menus or drops files onto it), opens only `http`, `https` and `mailto`
  links and the files the user gave the application through its open dialogs or
  a drop, each for that window, and bounds the rate; a refusal reports
  `PermissionDenied`, a turned-away rate `ResourceBusy`. A page's own copy and
  paste (Ctrl+C, Ctrl+V) use the host's web view and are not affected.
  **Your first window starts as the application's, not the user's:** the
  application opened it, so a button in it that copies, pastes or opens a link
  through these services gets `PermissionDenied` until the user switches away
  and back, clicks its native menu bar (not on GTK 4 yet) or drops files onto
  it. The same holds after the application presents, shows, maximizes or opens
  a window, also when the desktop activates it only later, such as macOS when
  the user next switches to the host. A host that started the application at
  the user's request can mark its first window as the user's
  (`RunicGuestStartInfo.UserStarted`). The clipboard's
  watch reports `BackendUnavailable`: a hosted application is not told when the
  user copies. Drags out of the window (`StartDragAsync`), the taskbar badge and
  progress report `OwnerUnavailable`, as in serve-only mode; notifications, the
  status icon, global shortcuts, settings and the secret store stay the
  application's own. Announcements (`AnnounceAsync`) go to the host's window,
  at a bounded rate; an assertive one from a window the user is not working in
  is made polite. `Present` and showing a window are rate-bounded too.
- **End.** When the host asks the application to shut down, or goes away
  (standard input ends or the channel closes), the Windows close,
  `DesktopHost.ShutdownRequested` and `RunicDesktopHost.Guest.HostGone` are
  cancelled and `Run` returns when the body does (event 2011). A Window that is
  opening or opens after that fails with `hosted-host-gone`; when the body ends
  with that failure, or with an `OperationCanceledException`, `Run` returns 0
  (event 2014): the host going away is the normal end of a hosted application,
  so the body need not catch it. A body that has not returned 10 seconds later
  ends the process (event 2012).
- `RunicDesktopHost.IsHosted` and `RunicDesktopHost.Guest` (the host's name and
  version) tell the mode. A hosted application does not claim
  `ApplicationInstance`: each host session starts a process of its own.
- Test it without a host application with `RunicTestHost` from
  `Runic.Application.Testing`
  ([a hosted application](../Runic.Application.Testing/README.md#a-hosted-application)).

### Hosting another application

The host side is `IRunicHostedApplications`, from `RunicDesktopHost.HostedApplications` or, after
`services.AddRunicHostedApplications()`, from the services. It starts the guest's executable with the parameter
that selects its hosted mode and shows each of the guest's Windows in a native window of the host (window mode):

```csharp
#pragma warning disable RUNICDESK103 // Hosted application windows are experimental.
var applications = host.HostedApplications;
applications.WindowOpened += (_, e) => Console.WriteLine($"{e.Window.Application.Guest.Name} opened {e.Window.Type}");
await using var tool = await applications.StartAsync(
    new RunicGuestStartInfo("/opt/tool/Tool", ["hosted"]),
    new RunicHostedApplicationOptions { Policy = new ToolWindowPolicy() });
var exitCode = await tool.Exited;
```

- **Start.** `StartAsync` starts the executable directly, never through a shell, with standard input and output
  piped, standard error and the other output lines in the log (events 2021 and 2022, `Debug`), and without
  `RUNIC_APPLICATION_SERVE_ONLY` and `RUNIC_APPLICATION_CLOSE_AFTER_OPEN`. On Windows the guest starts suspended,
  joins a kill-on-close job object and only then runs, so nothing it starts escapes the job, also when the host
  crashes; a job that cannot be created or joined fails the start. A batch file (`.bat`, `.cmd`) is refused with
  `ArgumentException`, since `cmd.exe` would parse its arguments. On Linux and macOS the guest leads a process group of
  its own, which ends with it; on Linux it inherits no descriptor but its standard streams (glibc 2.34 and later). A
  host that exits normally ends its guests' groups; a host that crashes or is killed cannot, and its guests exit when
  their standard input ends, within 10 seconds, while what a guest started itself may outlive it. The start writes the
  offer, reads the channel line, connects, without a proxy, and checks the guest's proof within `StartTimeout` (30
  seconds); a failure ends the guest's process tree and throws `RunicHostedApplicationException`
  (`guest-start-failed` for an executable that cannot be started, `guest-start-timeout`, `guest-exited`,
  `guest-not-hostable`, `guest-refused`, `protocol-unsupported`; event 2016). A shutdown or disposal of the host
  while a start is pending fails it with `InvalidOperationException` and ends the guest.
- **Windows.** Each guest Window becomes a `RunicHostedWindow`, whose `Presentation` is a native `DesktopWindow` of
  the host, embedded and without a browser fallback. It loads the guest's one-time launch address top-level from the
  guest's loopback origin: the host injects none of its credentials, admits no other origin for top-level navigation
  and gives each guest session a data store of its own that is not persisted. A user's close, and
  `RequestCloseAsync`, ask the guest first; the window closes when the guest's Window has. The guest's presentation
  requests (present, minimize, visibility, full screen, maximize, size, position, document edited, represented
  file, colour scheme, layout direction, announcements) apply to that window, and its activity, placement and colour
  scheme go back to the guest; a guest that reads slowly receives the latest placement and activity, not every
  change. `IRunicHostedApplications.WindowOpened` and `WindowClosed` report the windows, one after the other and in
  order: add the handlers before `StartAsync`. A window that finishes opening after its guest went away closes at
  once and raises neither.
- **Menus.** Each hosted window shows the guest's menu bar, never the host application's own
  (`RunicHostedWindow.MenuBar` is what it shows). On Windows and Linux it is the window's own bar, in the window or
  the desktop's global menu. On macOS the process has one menu bar: while a hosted window is key, it shows the host's
  application menu, whose About, Settings and Quit and Application-role items stay the host's, then the guest's
  menus: the guest's File menu in the File menu's place, its Help menu last, its others before the Window menu, and an
  Application-role menu of the guest as an ordinary menu under its own title. The standard Edit, View and Window
  menus stay in the host's language; the host's other menus give way until a window of the host is key again, and a
  shortcut both use is the host's. Shortcuts are matched by the host's native menus, and an item the user chooses
  runs the guest's command (`menu-invoke`); `DesktopWindow.TryExecuteMenuItemAsync` on `Presentation` chooses one
  from code. A guest's View or Window menu, titled as the host's standard one, joins it on macOS. The guest's context
  menus show as native context menus of its window, only while the window is active and inside its web view.
- **Dialogs.** The guest's file dialogs (open, open several, save, folder, folders) and message boxes show as the
  platform's own dialogs owned by the guest's window, one at a time; the guest receives the selected paths and opens
  them itself, and the host keeps no access to them: a sandboxed host or guest (App Sandbox, Flatpak) can receive
  paths it may not open, as the picker's grant stays with the process that showed it. Closing the window, the guest
  giving the dialog up or the guest going away closes a dialog that is shown. The titles, texts and button labels are
  the guest's; see [security](../../../docs/security.md#hosted-application-windows) for marking them.
- **Appearance and locale.** The guest starts with the host's application locale (`IRunicApplicationLocale`, after
  `AddRunicWindowService`), its user's languages (`DesktopLanguages.Preferred`) and the appearance its own windows
  show (from `IDesktopSettings`), and the host sends each change. A guest Window's locale gives its window the
  locale's direction, unless the host's window options fix one (`RunicHostedWindowRequest.Locale`).
- **Window services.** With `window-services/1` a guest uses the host window's clipboard, monitors and launcher, with
  the host's rights, and receives the files the user drops onto a window whose Window takes them; dropped files
  without a path, such as macOS file promises, are not forwarded. `IRunicHostedWindowPolicy.ServiceAsync` decides each
  request (`RunicHostedServiceRequest`); by default (`IsAllowedByDefault`) the host reads and writes the clipboard only
  for the window the user works in (active, and not within two seconds after any guest's request that can
  activate one of its windows, such as opening, presenting, maximizing or closing one, nor the first activation of a
  window the guest asked to activate while the desktop deferred it: `IsGuestActivated`; writes also
  within a second after the user left it), lists monitors for any window, and opens
  only `http`, `https` and `mailto` links and the files the user gave the guest through the host's open dialogs or
  drops, while each is still the same file, only for the window the user works in. A
  policy can widen or narrow this, change a launch (such as reveal instead of open) or forward only some dropped
  paths; see [security](../../../docs/security.md#hosted-application-windows) before widening it. The guest is never
  told when the clipboard changes, and cannot drag files out of its windows. A guest's windows start guest-activated,
  its first one included, so a link the user clicks in a fresh window is refused until the user switches to it. When
  the user started the guest directly, such as from the host's own menu, `RunicGuestStartInfo.UserStarted` makes the
  first activation of its first window the user's. It is off by default: the guest chooses when it opens that window,
  so with it set the guest can open it later, while the user works elsewhere, and still have it counted as the
  user's.
- **Limits.** `IRunicHostedWindowPolicy.Limits` (`RunicHostedLimits`) bounds a window's menu requests (20 a second by
  default; Runic's guest sends its latest menus again when one is turned away), the dialogs a guest shows at once,
  across its windows (one by default; another reports `ResourceBusy` to a Runic guest), its clipboard and monitor
  requests (20 a second), the files and links it opens (5 per ten seconds), its requests that can activate a window
  (present, minimize, set-visible, set-full-screen and toggle-maximized: 2 a second after a burst of 3) and its announcements (2 a second after a burst of 5). The host rebuilds a window's native bar
  at most every 50 milliseconds, however many changes arrive.
- **Permissions.** A guest's request for the camera and microphone is granted only as far as the host grants its
  own windows (`RunicDesktopHost.WindowOptions.AllowedPermissions`); a host that grants nothing grants a guest
  nothing. A policy can grant more through `RunicHostedWindowDecision.Allow(request.WindowOptions with { ... })`.
- **Policy.** `IRunicHostedWindowPolicy` decides each window (`OpenWindowAsync`: refuse, or open with other
  `DesktopWindowOptions` such as a saved placement keyed by `Guest.ApplicationId`) and each presentation request
  (`Present`: apply, clamp or refuse; the request a policy returns applies as it is), each menu bar and context menu
  (`Menu`: show, show changed entries, such as without an item or with an item of the host's own, whose command is
  then the host application's, or refuse), each dialog (`DialogAsync`: show, show with other options of the same
  kind, such as another initial directory, or refuse) and each window service (`ServiceAsync`). Without a policy,
  every window of the guest opens, every request, menu and dialog applies, and window services follow the defaults
  above.
- **End.** When the guest exits or crashes, or its channel closes, the host closes its windows, ends its process tree
  and completes `Exited` with its exit code (event 2017). `StopAsync` asks the guest to shut down and ends it after
  `StopTimeout` (10 seconds); `RequestStopAsync` asks each window first; `KillAsync` ends it at once. The guests
  still running stop when the host's Desktop shuts down and when `RunicDesktopHost.Run` returns. A hosted or
  serve-only application cannot host.

Each engine keeps a guest session's cookies, storage and caches apart from the host's and from other guests':

| Engine | Data store of a guest session | Isolation |
| --- | --- | --- |
| WebView2 | An InPrivate profile named for the session, in a user data folder of its own in the temporary folder that is deleted after the session | Cookies, storage and caches in memory, in a browser process of its own. The folder holds only the browser's own files. |
| WKWebView | `WKWebsiteDataStore.nonPersistentDataStore` | Cookies, storage and caches in memory, shared by the session's windows. |
| WebKitGTK 4.1 (GTK 3) | An ephemeral `WebKitWebContext` | Cookies, storage and caches in memory, in a network process of its own. Spell checking and the page language are set when the session's first window opens. |
| WebKitGTK 6 (`Runic.Desktop.Gtk4`) | An ephemeral `WebKitNetworkSession` | Cookies, storage and caches in memory. |

On WKWebView and WebKitGTK the store lives only while a window of the session is open: a guest that opens a window
after all its windows closed starts with an empty store, cookies, localStorage and IndexedDB included. On WebView2 the
store lives for the whole session. HTTP authentication, client certificates and the system's proxy and
trust settings are the engine's and the user's, not the store's. A custom `IDesktopWindowHostFactory` shows hosted
windows only if its window host implements `IDesktopEphemeralDataStoreWindowHost`.

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

An application that serves its Windows on purpose, such as a `--browser`
command, sets `RunicDesktopHostOptions.ServeOnly = true` instead (since 0.14):
the same mode, without an environment variable that the application's child
processes would inherit. `RunicDesktopHost.ServeOnly` tells which mode a host
is in. With either switch, `RunicDesktopHost.Run` starts no window toolkit loop,
so `WithGtk4()` on a machine without a display reports no
`gtk4-display-unavailable`. Open the served page in the user's default browser
with the application's launcher, `DesktopPlatformServices.ForApplication()`,
which needs no window:

```csharp
return RunicDesktopHost.Run(provider, new DesktopHostOptions().WithGtk4(),
    new RunicDesktopHostOptions { ServeOnly = true }, async host =>
{
    await using var window = await host.OpenWindowAsync<MainWindow>();
    await DesktopPlatformServices.ForApplication().FileLauncher.LaunchUriAsync(RunicDesktopHost.GetSurface(window)!.Url);
    await window.WaitForCloseAsync();
    return 0;
});
```

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
- **Window.** Directly after its URL line, in the same write, each window
  writes `RUNIC_APPLICATION_WINDOW=<url> <type>`: its URL again and the full
  name of its Window class, such as `MyApp.SettingsWindow`, separated by a
  space. The type has no spaces: a closed generic Window type is written as
  its generic definition's full name with its arguments' names without
  namespaces, such as ``MyApp.DocumentWindow`1[Report]``. Hosts before 0.16 write no such line, and a parser that only reads
  `RUNIC_APPLICATION_URL=` lines ignores it. Read the first two
  space-separated fields; a later release may add more after them.
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
