# Runic.Application.Views.CsWebUi

Present Runic Windows and Views with [CS-WebUI](https://github.com/Runic-Artifex/cs-webui).
New projects select it with `dotnet new runic-app --host cswebui`; it opens the
app in a browser window and is supported on a best-effort basis. The template
defaults to the native [Runic Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md) host.

```sh
dotnet add package Runic.Application.Views.CsWebUi --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyApp; // the generated AddRunicViews()
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
services.AddScoped<MainViewModel>();
services.AddRunicViews(); // generated in <RootNamespace>.RunicViewsComposition
using var provider = services.BuildServiceProvider();

return RunicCsWebUiHost.Run(provider, async host =>
{
    host.ValidateWindow<MainWindow>().ThrowIfInvalid(); // stops at startup when the Bridge is not registered
    await using var window = await host.OpenWindowAsync<MainWindow>(new RunicWindowOptions { Width = 800, Height = 600 });
    await window.WaitForCloseAsync();
    return 0;
});

public sealed partial class MainWindow : RunicWindow<MainViewModel>;
```

The same Window and `async host => ...` body run natively with
`RunicDesktopHost.Run(provider, ...)` from `Runic.Application.Views.Desktop`.

`RunicCsWebUiHost.Run` runs the asynchronous application on the thread pool
while the process main thread shows its windows and waits in WebUI's wait loop,
as CS-WebUI expects. It calls `WebUiApplication.Clean()` before it returns.
`OpenWindowAsync` creates a DI scope, resolves the Window's ViewModel, assigns
it to the Window's `DataContext`, attaches the generated Bridge, sets the
content root and size from `RunicWindowOptions`, and shows `StartPage`. The
Window owns the native window, scope, and attachments until it is disposed or
`CloseAsync` drains its accepted operations. Its `Host` is a
`CsWebUiBridgeWindow<TViewModel>`; `Window` exposes the underlying
`WebUiWindow`; `CsWebUiPlatformServices.GetWebUiWindow(window)` returns it for
any Window of this host without naming the ViewModel type, and `null` for
another host's. `WaitForCloseAsync` completes when WebUI reports that its
windows have closed: CS-WebUI reports closing for all of its windows at once,
so with several windows open it waits for all of them.

A Window with `GuardClose` keeps a `UseWebView` window on Windows and Linux open
while `CanCloseAsync` decides the user's close; elsewhere it opens unguarded
(see [Closing a window](#closing-a-window)).

`ValidateWindow<TWindow>` checks at startup that the generated Bridge for the
ViewModel is registered and returns a `RunicWindowValidationResult`, the same
result Runic Desktop returns. `OpenWindowAsync` repeats the check before it
creates the native window. A missing `AddRunicViews()` throws a
`RunicWindowConfigurationException` (an `InvalidOperationException`) whose
diagnostic has the code `bridge-not-registered`, the same code Runic Desktop
reports, and a remediation, instead of a dependency-injection error. It is
logged as event 1050 through the provider's `ILoggerFactory`.
`ValidateWindow` asks the container through `IServiceProviderIsService`; a
container without it is checked only when `OpenWindowAsync` resolves the Bridge.

Both hosts share one lifetime contract, `IBridgeWindow` from
`Runic.Application.Views`: `CloseAsync(timeout, cancellationToken)` returns a
`RunicWindowCloseResult`, and disposal is asynchronous only. Accepted
operations can keep the Window's DI scope alive after the visible window
closes, so dispose the Window with `await using` before the application returns.

Pushes to every client, such as the `state` and `frame` pushes of the Views
protocol, are written in order as WebSocket messages to every connected client
(WebUI raw data). The writes block until each socket accepts the push or the
write times out, so a slow browser holds back delivery at the TCP level.
Frames queue in the bridge, which sends a recovery state once the queue is
full, and the window's other clients wait for the same write. Other CS-WebUI windows are not held back:
WebUI before nightly `9264f7ae9` (webui-dev/webui#723) wrote every WebSocket
frame under one process-wide lock, so one stalled client delayed every window in
the process by up to 30 seconds per frame. CsWebUi 2.5.0-beta.4.10, the lowest
version this package accepts, includes the fix.

## What a window opens

By default a window uses WebUI's recommended presentation: an installed browser
in app mode (Chrome, Edge and other Chromium-based browsers work best; Firefox
works without app mode), then the system default browser, then the platform
WebView. `RunicCsWebUiHostOptions.UseWebView` always uses the WebView: the Edge
WebView2 Runtime on Windows, GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on
macOS. Pass the options to `Run`, which keeps WebUI's main-thread loop:

```csharp
return RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions { UseWebView = true }, async host =>
{
    await using var window = await host.OpenWindowAsync<MainWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
```

`ConfigureWindow` configures each `WebUiWindow`
before it is shown, for example `SetPort`, and `ShowWindow` replaces how it is
shown, for example to open a frontend development server's URL. To choose one
browser, set `CSWEBUI_BROWSER_PATH` to its executable (on macOS its `.app`
bundle) or call `WebUiApplication.SetBrowserPath`; WebUI then uses only that
browser, with no fallback. WebUI itself reads no browser variable such as
`WEBUI_BROWSER_PATH`.

On macOS this package has no native window mode, and none is planned: a window
is a browser window, or with `UseWebView` the WKWebView window that WebUI creates
and runs from its own main-thread loop. Runic does not run AppKit for it, so
CS-WebUI windows on macOS get no Runic window features (native menu bar, close
confirmation, file drops, window-owned platform services), as on the other
platforms, and Runic's CI does not exercise `UseWebView` there. An application
that needs a native macOS window uses Runic Desktop, whose `RunicDesktopHost.Run`
runs the Desktop event loop on the macOS main thread; the same Windows and
ViewModels run on both hosts. For native
windows and platform services, use
[Runic.Application.Views.Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md)
instead. `services.AddRunicPlatformServices()` registers `IWindowPlatformServices`,
`IFileDialogs`, `ITextClipboard` and `IDesktopFileLauncher` per window here too, so
ViewModels that take them resolve on both hosts. CS-WebUI windows have no native
owner, so every operation, `AnnounceAsync` included, and the capability snapshot report
`Unavailable(OwnerUnavailable)`. It also registers one `IDesktopSettings`,
`IDesktopNotifications`, `IDesktopStatusIcon`, `IDesktopLoginStartup` and
`IDesktopInhibition`, the same application services as Runic Desktop, whose
requests return `Unavailable(ProviderNotConfigured)`: this package selects no
native provider. They need no window, so register a platform provider's service,
such as `WindowsPlatformProvider.CreateSettings()` or
`new PortalApplication("org.example.Notes").CreateInhibition()`, before
it to use one on CS-WebUI. The
[Runic.Platform guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#hosts-without-a-native-owner)
lists each service's result on both hosts.

File dialogs need no owner window on the user's own desktop. Pass a provider's
unowned dialogs to `AddRunicPlatformServices`, and each window's `IFileDialogs`
shows them for `OwnerPolicy.PreferOwner`, as Runic Desktop does for a window
without native dispatch:

```csharp
if (OperatingSystem.IsWindowsVersionAtLeast(8))
    services.AddRunicPlatformServices(WindowsPlatformProvider.CreateUnownedFileDialogs());
else if (OperatingSystem.IsMacOS())
    services.AddRunicPlatformServices(MacOSPlatformProvider.CreateUnownedFileDialogs());
else if (OperatingSystem.IsLinux())
    services.AddRunicPlatformServices(new PortalApplication("org.example.Notes").CreateUnparentedFileDialogs());
else
    services.AddRunicPlatformServices();

// In a ViewModel that takes IFileDialogs:
if (await files.OpenDirectoryAsync(new(OwnerPolicy.PreferOwner) { Title = "Open workspace" })
    is PickerResult<IDirectoryLease>.Selected(var folder))
    Open(folder.LocalPath); // the lease keeps access until it or the window is disposed
```

The application references the provider packages it passes (Runic.Platform.Windows,
Runic.Platform.MacOS, Runic.Platform.Linux.Portal); this package still selects
none. Open, multiple, directory, document and save selection work as on Runic
Desktop, with the request's title, filters, initial directory and suggested name;
the buttons are the platform's own. Cancellation closes a shown dialog. A
`RequireOwner` request, the options' default, still reports `OwnerUnavailable`,
and the capabilities report `platform.dialogs.unowned` available and
`platform.dialogs.owned` unavailable. A window shows one dialog at a time
(`ResourceBusy` otherwise); closing it closes its dialog and releases its leases.
A dialog opens only for a window that no other computer can reach, because the
user at this computer answers it; each request checks again. A window gets no
dialogs (`OwnerUnavailable`) under `RUNIC_APPLICATION_SERVE_ONLY=1`, when
`RunicCsWebUiHostOptions.ShowWindow` shows it (it may only start the server for
another device), or when its server listens beyond loopback, as
`WebUiWindow.SetPublic(true)` makes it. Windows shows each dialog on an STA thread
of its own, so two windows' dialogs close independently. macOS shows AppKit panels
on the main thread: `Run` blocks in the main run loop while idle and while browser
windows are shown, with WebUI's wait on a thread of its own, and a panel then runs
modally until it closes; a second window's panel waits for it. With `UseWebView`,
WebUI's own AppKit loop shows panels without blocking. Without registered dialogs
the main thread blocks as WebUI's wait does. A host created with its constructor
leaves the main thread to the application, which must serve the main queue for the
panels. An owner-less dialog may open behind the browser: Windows' foreground rules
let only the foreground application bring a window forward, and macOS 14 and later
activate the application only when the browser yields; macOS still orders the
panel in front of other applications' windows.

A root ViewModel that implements `IRunicFileDropTarget` is
never called here: a browser window reports no dropped paths, so the page only
gets HTML5 `File` objects. To bring one into .NET, upload it: `client.upload(file)`
streams it to a loopback endpoint of the window, and a command takes the
resulting reference as a `RunicUpload` (see
[Blobs and uploads](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#blobs-and-uploads)).
`RunicBlob` values, such as images and downloads the ViewModel produces, are
served from the same endpoint, `http://localhost:{port}/` (the window holds
both `::1` and `127.0.0.1` on that port), another origin than the page, which it grants with CORS, and only
to loopback peers; a page `Content-Security-Policy` must allow
`http://localhost:*` for `img-src`, `media-src` and `connect-src`. That
endpoint cannot see WebUI's clients, so a blob URL is bound to the window, not
to one browser; an upload also needs its connection's token. There is no native menu bar either: the page renders
`RunicCsWebUiHostOptions.MenuBar` (see [Menu bar](#menu-bar)).

A ViewModel's `IRunicWindowPresentation` does what WebUI can:
`PresentAsync` brings the window to the front (`WebUiWindow.Focus`), and
`MinimizeAsync` minimizes a `UseWebView` window. A `UseWebView` window on
Windows and Linux also reports its placement and maximizes and restores (see
[Window placement](#window-placement)). The rest, hiding, full screen,
the edited mark and the color scheme, reports `Unavailable(BackendUnavailable)`.
A browser window tells WebUI nothing about its focus, so the Window reports no
activity: `Features` lacks `Activity` and the Window counts as active and
visible (`RunicWindowActivity.Assumed`). A Window served under
`RUNIC_APPLICATION_SERVE_ONLY=1` has no window, and its requests report
`Unavailable(OwnerUnavailable)`.

CS-WebUI cannot own windows (B6, since 0.16): `Features` lacks `OwnedWindows`,
and a Window opened with `RunicWindowOptions.Owner` or `Modality` opens on its
own and logs `WindowOwnerIgnored` (event 1180). `ValidateWindow` reports the
warning `window-owner-unsupported` (`RunicCsWebUiHost.OwnerUnsupportedCode`).
`IRunicWindowService.FindOrOpenAsync`, `ActivateAsync` and `OpenForResultAsync`
work as on Runic Desktop, with the activation this host has.

When WebUI cannot show a window, because it finds no browser, the selected
browser does not exist or does not start, or the page does not connect within
the connection timeout, `OpenWindowAsync` throws a
`RunicWindowConfigurationException`, as Runic Desktop does when no presentation
can open. Its diagnostic names the browser, its path and the reason, with the
code `browser-not-found`, `browser-not-started`, `browser-not-connected`,
`webview-not-started` or, for another WebUI error such as an unusable
`CSWEBUI_BROWSER_PATH`, `window-show-failed`. The `WebUiShowException` (or
`WebUiException`) is its inner exception. The host logs the failure as event
2100 `CsWebUiWindowShowFailed` in the category `Runic.Application.Views.CsWebUi`,
or writes it to `Trace` without an `ILoggerFactory`. When the application does
not handle the exception, `RunicCsWebUiHost.Run` writes its message to standard
error and returns 1 instead of ending the process with an unhandled exception.
WebUI starts browsers detached and discards their output; run the browser with
the window's URL to see why it fails.

`ValidateWindow` reports, with the code `browser-not-found`, a
`CSWEBUI_BROWSER_PATH` that names no file, because WebUI uses that browser
without fallback and every open fails. It cannot check more without showing a
window: WebUI's recommended presentation falls back from an installed browser
to the system default browser and then to the WebView, and the WebView runtime
(for `UseWebView` too) is loaded only when a window shows. Those failures are
reported by `OpenWindowAsync` as above. `ShowWindow` and serve-only mode skip
the check.

Set `RUNIC_APPLICATION_SERVE_ONLY=1` to start the local server without opening
anything. Each window then writes `RUNIC_APPLICATION_URL=<url>` and
`RUNIC_APPLICATION_WINDOW=<url> <Window type>` to standard output, ignoring `UseWebView` and `ShowWindow`, and keeps serving
until a line is read from standard input or the process ends, so a test
harness or remote browser can drive the unchanged application. Write the line
on Windows too; the
[serve-only protocol](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md#automation)
is the same as on Runic Desktop, and `RunicServeOnlyApplication` in
[Runic.Application.Testing](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md#the-whole-application-in-a-browser)
implements it for browser tests. In serve-only mode `ValidateWindow` checks no
browser. On Linux with GTK 3 and WebKitGTK 4.1 installed, WebUI initializes
GTK when it creates a window, also in serve-only mode, and GTK ends the process
when no display opens. Give a headless run a display, for example with
`xvfb-run`.

## Closing a window

Since 0.23 (#1148) a `UseWebView` window asks before it closes on Windows and
Linux: when the user closes a Window with `GuardClose`, with the close button,
Alt+F4 or the window manager, the window stays open while `CanCloseAsync`
decides in a model turn, as on Runic Desktop. When it allows, the host closes
the window with `WebUiWindow.Close`, which CS-WebUI never sends through the
close handler again; when it refuses, the window stays as it was. A second
close while the guard asks is ignored. `window.RequestCloseAsync()` asks the
same guard and joins a decision the user started, so the two never ask at
once; an allowed request releases the Window as before. `CloseAsync`,
disposal and the end of `Run` close without asking and cancel a pending
decision.

| Window | The user's close of a guarded Window |
| --- | --- |
| `UseWebView` on Windows (WebView2) | Guarded (`WM_CLOSE`) |
| `UseWebView` on Linux (WebKitGTK, X11 and Wayland) | Guarded (GTK `delete-event`) |
| `UseWebView` on macOS (WKWebView) | Unguarded, event 1053: WebUI learns of the close only afterwards |
| Browser window (the default presentation) | Unguarded, event 1053: the browser closes its window without asking |
| `RunicCsWebUiHostOptions.ShowWindow` | Unguarded, event 1053: the host cannot tell how the window is shown |
| `RUNIC_APPLICATION_SERVE_ONLY=1` | Unguarded, event 1053: there is no window |

Where the user's close cannot be guarded, a Window with `GuardClose` opens
unguarded, logs event 1053 `WindowCloseGuardUnavailable` once naming the
reason, and its `CanCloseAsync` is not asked when the user closes the window;
the navigation regions' departure guards still run for navigation inside the
window, and `RequestCloseAsync` still asks `CanCloseAsync` before it closes
the window, because the application is the one closing. The host sets its
close handler before `ConfigureWindow` runs, so an application that calls
`WebUiWindow.SetCloseHandler` there replaces the guard for the user's close.

### Closing after the Window's work has drained

Since 0.23 (#1190) a Window with a close drain (`RunicWindowOptions.CloseDrain`,
or `RunicCsWebUiHostOptions.CloseDrain` for every Window) keeps a `UseWebView`
window on Windows and Linux open after its close was decided, the user's once
the guard agreed or `RequestCloseAsync`'s, as Runic Desktop's embedded window
does: operation admission stops, the page receives `closing` with
`draining: true` and stays connected, and the window closes through
`WebUiWindow.Close` once the Window's operations, awaited commands and
drained scopes have ended or the drain's time passed. A user close meanwhile is
ignored. `RequestCloseAsync` returns once the close is decided, and
`WaitForCloseAsync` once the window closes. `CloseAsync(timeout)` during the
drain ends it at its timeout, and `DisposeAsync` at once (see
[Closing after the Window's work has drained](../Runic.Application.Views/README.md#closing-after-the-windows-work-has-drained)).

Under `RUNIC_APPLICATION_SERVE_ONLY=1` the end of serving is the Window's close:
a line on standard input. A closing browser tab, the last one included, is no
close, because the server outlives its clients and a reload connects again.
With a drain, the line makes every serving Window with one drain first, its
pages still connected and served, and WebUI's wait, and with it
`WaitForCloseAsync`, ends once they have all drained. Lines read meanwhile are
ignored.

| Window | Close drain |
| --- | --- |
| `UseWebView` on Windows (WebView2) and Linux (WebKitGTK) | Yes |
| `RUNIC_APPLICATION_SERVE_ONLY=1` | Yes, when serving ends |
| `UseWebView` on macOS (WKWebView) | No: the window closes at once, event 1055; WebUI learns of the close only afterwards |
| Browser window | No: event 1055; the browser closes its window without asking, and no Runic host can keep an installed browser's window open (D077.3) |
| `RunicCsWebUiHostOptions.ShowWindow` | No: event 1055; the host cannot tell how the window is shown |

The CS-WebUI host handles no termination signal (Ctrl+C, SIGTERM): the process
ends without a drain. Where a Window does not drain, its scope still waits for
durable work.

## Window placement

Since 0.23 (#1149) a `UseWebView` window on Windows and Linux reports its
placement, the restored bounds and whether it is maximized, in
`IRunicWindowPresentation.Placement`, and takes one with `SetPlacementAsync`,
`MaximizeAsync` and `RestoreAsync`; `Features` has `Placement` and `Maximize`.
Save the placement when the Window closes and pass it back with
`RunicCsWebUiHostOptions.Placement` at the next start, as with Runic Desktop's
`RunicDesktopHostOptions.Placement`:

```csharp
return RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions
{
    UseWebView = true,
    Placement = window => window is MainWindow ? settings.MainWindowPlacement : null,
}, async host =>
{
    await using var window = await host.OpenWindowAsync<MainWindow>();
    await window.WaitForCloseAsync();
    settings.MainWindowPlacement = window.Presentation.Placement; // also after the user closed it
    return 0;
});
```

The host reads the placement every 500 ms while the window is shown, and once
more when it closes, also when the user closes it: WebUI raises no event when a
window moves or resizes. A Win32 subclass could report `WM_WINDOWPOSCHANGED`
on Windows, but only on the window's own thread, which runs WebUI's message
loop, and GTK offers no signal to another thread, so both platforms poll; a
read is a few user32 calls on Windows and one turn of GTK's main loop on
Linux. `PropertyChanged` reports a change in a model turn, so a drag reaches
the ViewModel as a few changes, not one per pixel. A placement, the initial one
included, is kept on a monitor that exists now: bounds off every monitor's
work area move onto the one they are nearest, and bounds larger than it shrink
to fit, by Runic Desktop's rule.

| Window | Reported | Taken |
| --- | --- | --- |
| `UseWebView` on Windows (WebView2) | Restored bounds (outer frame, screen coordinates), maximized | Bounds, maximized |
| `UseWebView` on Linux, X11 | Size, frame position, maximized | Size, position, maximized |
| `UseWebView` on Linux, Wayland | Size and maximized; `X` and `Y` are `null` | Size and maximized; a placement with a position reports `Unavailable(BackendUnavailable)` |
| `UseWebView` on macOS (WKWebView) | Nothing (`Placement` is `null`) | The initial size and a position WebUI accepts, before the window shows |
| Browser window, `ShowWindow` | Nothing | The initial size and a position WebUI accepts, before the window shows |
| `RUNIC_APPLICATION_SERVE_ONLY=1` | Nothing | Nothing: there is no window, and `Placement` is not called |

- The units are CS-WebUI's (`WebUiWindowPlacement`): on Windows the outer
  window rectangle in screen coordinates, scaled as the process's DPI
  awareness scales them; on Linux GTK's window size, without the frame, and the
  frame's position. They are not Runic Desktop's, so save a placement per host.
- Before a window shows, WebUI takes positions from 0 × 0 to 3000 × 1800 and
  sizes from 100 to 3840 pixels only; a WebView window takes the whole
  placement, a negative position included, once it is shown.
- CS-WebUI has no full screen: a placement with `FullScreen` reports
  `Unavailable(BackendUnavailable)`. On Linux `RestoreAsync` un-maximizes but
  does not bring back a minimized window.
- A Linux window manager decides whether to maximize and where to put a window;
  without one, as under a bare Xvfb, a window does not maximize.

## The page in a JavaScript debugger

In Development, a browser window's Chrome or Edge opens its DevTools Protocol
endpoint on 127.0.0.1 when `RUNIC_WEBUI_REMOTE_DEBUGGING_PORT` asks for it
(since 0.23, #1221): `dotnet runic dev --debug-endpoint`, `--no-window` and the
template's `(CS-WebUI script debugging)` profile set it, and the Window prints
`RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:<port>/` once
the endpoint answers, which `dev` turns into its `[dev] Debugging:` line. A
`UseWebView`, `ShowWindow` or serve-only window opens none, nor does a
non-Chromium browser or a trimmed or NativeAOT publish. The host passes WebUI's
Chromium defaults with the port as the window's custom parameters, before
`ConfigureWindow`. See
[CS-WebUI pages in a JavaScript debugger](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#cs-webui-pages-in-a-javascript-debugger).

## Menu bar

A CS-WebUI window has no native menu bar, so the page renders the host's bar
with its own menubar component, as a Runic Desktop window with
`DesktopMenuBarPresentation.Page` does. Declare the menus with
`RunicCsWebUiHostOptions.MenuBar`, the same `DesktopMenu` model and rules as
Runic Desktop's `DesktopHostOptions.MenuBar`, and run them with the Window's
root ViewModel (`IRunicMenuCommandSource`):

```csharp
return RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions
{
    MenuBar =
    [
        new DesktopMenu("File", [new DesktopMenuItem("Save", "file.save") { Shortcut = new DesktopShortcut("S") }])
            { Role = DesktopMenuRole.File, AccessKey = 'F' },
    ],
}, async host => { ... });
```

The page reads the bar with `createMenuBarController()` from
`@runic-artifex/views`, or `useMenuBar` and `injectMenuBar` in the framework
adapters; see
[In-page menu bar](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/web/views/README.md#in-page-menu-bar).
The bar is served on the reserved route `runic.menu-bar`, with menu bar page
protocol 2 as on Runic Desktop
([specs/desktop/menu-bar-page.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/menu-bar-page.md)),
from the same source, so the same frontend runs on both hosts. Its pushes to
one connection travel through the connection's mailbox
([Requests and pushes](#requests-and-pushes)). On CS-WebUI:

- `placement` is `"page"` on every platform while the host declares menus, and
  `"none"` with an empty bar.
- An item is enabled while its `IsEnabled` and its command's
  `CanExecute(CommandParameter)` are `true`, following `CanExecuteChanged`, and
  `invoke` runs `Execute(CommandParameter)` in the ViewModel's model turn. The
  host asks the ViewModel for each id when the Window opens and after
  `SetMenuBar`. There are no application commands, so an id the ViewModel
  returns `null` for stays disabled. A failure is logged as event 2102,
  `CsWebUiMenuCommandFailed`.
- WebUI reports no keys to .NET, so the page's controller matches the items'
  shortcuts (`shortcuts` `"page"`), and `shortcuts: "app"` leaves them to the
  page's own handler.
- `host.SetMenuBar(menus)` replaces the bar in every Window of the host, for
  example with new checked state, and `host.MenuBar` returns it.

## Build output

The package's build targets copy its Views client into `www/` next to the
built frontend as `runic-views.js`. With
`RunicApplicationFrontendDelivery=Embedded` (since 0.15) they pack the frontend
and the client into the application assembly instead, and each window serves
that archive without extracting it; see
[Ship the frontend inside the executable](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#ship-the-frontend-inside-the-executable).
`RunicCsWebUiHostOptions.Assets` serves another `IAssetSource`, such as one
folder of an archive (`archive.Subdirectory("settings")`). A window serves the
source through `WebUiWindow.SetFileHandler`: WebUI still serves `webui.js`, a
path the source does not declare answers 404, and a Window whose
`RunicWindowOptions.ContentRoot` is set serves that folder instead. Since a
file handler turns WebUI's authentication cookie off for the whole process,
the host turns it back on after installing its handler (see [Security](#security)). Load `webui.js` (served by WebUI) and
`runic-views.js` before the generated client in `index.html`; the same page then
runs on Runic Desktop, whose package copies its own client as `runic-views.js`.
A project that references both hosts sets `RunicApplicationHost` (`cswebui` or
`desktop`); see
[the host of a page](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#the-host-of-a-page).
Since 0.23 the build no longer copies the client as `runic-cswebui.js`
(deprecated in 0.22): a page that loads that name gets a 404 and no Bridge, and
the build reports warning `RUNICBRIDGE019`. The script adapts
`window.webui` to the generated client's transport contract, `__runicBridge`.
Load it right after `webui.js`: it then observes WebUI's connection events
through `webui.setEventCallback` and passes every event on to the callback the
application sets there, before or after. As with any event callback, WebUI then
shows no "Connection with the backend is lost" banner; the page shows the
connection state itself. Loaded after WebUI created `window.webui`, the script
checks the connection every 250 ms instead.

The script sends each Bridge request as soon as the page makes it, so several
requests can reach WebUI at once. Runic relies on WebUI's fix for concurrent calls
(webui-dev/webui#721): before it, native WebUI could assign one event slot to
two calls that arrive together. One call could then run with the other's
arguments or receive the other's reply, and one of them could receive no reply
at all. The fix is in WebUI nightly `efac0e24d` and in every CsWebUi since
2.5.0-beta.4.7. CsWebUi 2.5.0-beta.4.10, the lowest version this package
accepts, bundles WebUI nightly `24c7d196edc63f5b777d465063c3d26731f52dea`,
which also removes the process-wide send lock (webui-dev/webui#723) and gives
navigation events their URL argument (webui-dev/webui#724). An application that
loads its own WebUI through `CSWEBUI_NATIVE_LIBRARY` or
`WebUiNativeLibrary.SetLibraryPath` needs `efac0e24d` or later for concurrent
calls, and `2b35db13f` or later for the behaviour this package is tested with.
WebUI reports no version or commit that Runic could check, so the host logs
event 2101 `CsWebUiNativeLibraryUnchecked` (Warning) once, before the first
window opens, when such a library is configured.
Applications that keep their own copy of the client must update it: the
script and the host speak one Views protocol, 4 in this release. A copy from
an earlier release speaks Views protocol 3, so its Views do not connect. The build replaces the
copy in `www/` on every build, so only a copy the application keeps elsewhere,
such as in a frontend's `public/` folder, can be stale. The host sends pushes
to the script's `__runicViewsPush` function and carries out navigations
through `__runicContinueNavigation`, both as WebUI raw data, so the page's
Content-Security-Policy needs no `'unsafe-eval'`.

The window binds all WebUI events because only that binding delivers
disconnects. The script stops WebUI from also sending a click event for every
element with an id: those clicks would reach the server in parallel with the
Bridge calls they start. Use Bridge commands rather than WebUI element bindings
in a Views window.

With that binding, WebUI also cancels the page's own navigations after each
connect until a Bridge call allows them, and reports each one to the server.
The host carries out a same-origin one, such as a reload, a link to another
page or a fragment, so the page does not stay put before its first Bridge call.
A cross-origin navigation and a form submission stay cancelled.

### Requests and pushes

`CreateBridgeSession()` returns the window's `IBridgeTransport`, with the same
signature as before. It binds one native function, `__runicViews`, which the
script calls with each Views protocol 4 request; routes and members travel
inside the request, so Views that come and go add no bindings. CS-WebUI cannot
remove a binding before its window closes, so the binding stays while the
window's request handler changes, and a request while none is served gets the
`released` reply. Dispose the transport before the `WebUiWindow`. The trusted
client key is WebUI's client id, and the connection key `clientId:connectionId`.
WebUI reuses connection slots, and without its authentication cookie every
client id is 0, so a reloaded page or another browser can get the key of a
connection that just closed, even before WebUI reports that close. Since 0.16:

- The window gives every connection its own key, `clientId:connectionId#n`,
  with a counter that never repeats, so a command, question or push of a
  closed connection never reaches the connection that reused its slot.
- With `request-session/1`, which pages and hosts of 0.16 use, every request
  names the random session of its page's `hello`. A request of another page,
  including the `receive` that `runic-views.js` sends as soon as its socket
  opens, is answered `no-hello` and never touches the old connection or its
  mailbox, and the new page's `hello` closes the old connection at once
  ([request sessions](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/README.md#request-sessions)).
  A page of 0.15, which sends no session, is not protected this way while its
  close is not reported.

- `BroadcastAsync` writes a push to every client of the window with WebUI's
  `SendRaw`.
- WebUI cannot send to one client outside an event, so the transport does not
  implement `IBridgeConnectionTransport`. The window queues each connection's
  targeted pushes, such as operation status, interactions and the in-page menu
  bar, in a mailbox, and the script drains it with a `receive` loop: one
  `receive` request pending per connection, answered as soon as a push is
  queued or with an empty list after 25 seconds. A mailbox keeps at most 1,024
  pushes and 4 MiB; beyond that it drops its oldest pushes and logs event 1023
  `BridgeMailboxOverflowed` once per connection.
- A pending `receive` holds one native WebUI thread. WebUI runs every call on
  its own thread, so it does not hold up the client's other requests, but it
  does not keep their order either: a request may be admitted before one the
  page sent earlier. Edits carry their own numbers, so a later edit still wins,
  and a broadcast and a mailbox push are not ordered relative to each other.

### Several clients

With WebUI's `MultiClient` configuration, every browser that reaches the
window's server shares the window: they see one ViewModel, and broadcasts
reach them all. `IRunicConnections` (since 0.16), which
`AddRunicWindowService()` registers per Window scope, lists the window's
connections and raises `Connected` and `Disconnected` in a model turn. Its
`Caller`, read in a command, an operation or an in-page menu click, is the
connection that sent it. The keys are the trusted keys above (WebUI's client
id, and `clientId:connectionId`), never values the page sends; a reload is a
new connection. See
[Who is connected, and who called](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#who-is-connected-and-who-called)
for the API, `RunicConnectionState<T>` for a value per connection, and a
"who else is viewing" example.

A connection key says which page sent a request, not who the user is: WebUI
admits every client that reaches the server (see [Security](#security)).
`MultiClient` needs the application's own authentication, for example a
sign-in command that maps the caller's `ConnectionKey` to a user the
application checked, kept in a `RunicConnectionState<T>` so it goes when the
page disconnects.

**One language per window.** A shared window shows one language to every
browser: the window's locale, which the application sets
(`IRunicWindowLocale`). Each connection reports
its browser's preferred languages (`RunicConnection.Languages`, since 0.17),
so the application can choose the window's language from them, for example
when the first browser connects. Negotiate them against the locales the
application ships and set the result; never assign a browser's tag directly,
or one remote browser picks an arbitrary language for everyone. A page that
offers a language switch asks the application with a command, which switches
the window for everyone.

The languages are personal data that can help fingerprint a browser, and they
reach this server from every remote browser that connects. Use them to choose
the window's language only, and do not log or store them. A page can stop
sending them with `configureClientLanguages({ send: false })` from
`@runic-artifex/views`, called in `main.ts` before it connects.

## Runic's own text

The text Runic itself shows in a CS-WebUI application follows the same hooks
as on Runic Desktop: validation messages take `RunicValidationOptions.TextFor`
in every window, and the platform services' dialogs (where the platform has no
translated text of its own) and the in-page bar's shortcut labels take
`DesktopPlatformTexts.Current` (the bar shows English Ctrl/Shift/Alt unless the
application names them).
[`Runic.Application.Views.Translations`](../Runic.Application.Views.Translations/README.md)
keeps both in the application's language.

The pages and banners CS-WebUI shows itself take `DesktopPlatformTexts.Current`
too (since 0.23, cs-webui#32), with the window's locale as their language:

| Text | `DesktopPlatformTexts` | Shown |
| --- | --- | --- |
| **Resource Not Available** page | `ResourceUnavailableTitle`, `ResourceUnavailableMessage` | A file an embedded frontend (`Assets`, `RunicApplicationFrontendDelivery=Embedded`) does not have |
| **Internal Server Error** page | `ServerErrorTitle`, `ServerErrorMessage` | The window's file handler fails |
| Connection banners of WebUI's bridge | `ConnectionErrorLabel`, `ConnectionFailedMessage`, `WebSocketUnsupportedMessage`, `ConnectionLostMessage` | The page cannot connect within five seconds, or the browser has no WebSocket |

The host gives the window CS-WebUI's `WebUiTexts` before each response, and
`runic-views.js` loads their banner script from the window's
`/_runic/cswebui/texts.js`. The script recognizes WebUI's banners by their
element id and English text at the pinned WebUI, so a future WebUI may show
them in English again. `runic-views.js` handles a lost connection itself, so
`ConnectionLostMessage` shows only in a page without it. A window served from a
web root folder gets the same file handler, for the banner script alone, and
keeps WebUI's authentication cookie on, as an embedded frontend does. A file
handler that the application installs itself, through `ConfigureWindow` or
`ShowWindow`, replaces the host's, and the banners stay English.

Two pages are WebUI's own and stay English (D061.8): **Access Denied**, which a
second browser gets when `MultiClient` is off and another one already shows the
window, and WebUI's **Resource Not Available** for a file a web root folder
(`RunicWindowOptions.ContentRoot`, `www/`) does not have. WebUI sends them
itself, before or without the file handler, and has no way to replace them, so
`DesktopPlatformTexts`' `AccessDeniedTitle` and `ContentInUseMessage` apply to
Runic Desktop's browser windows only.

## Security

This host adds no admission of its own. Runic Desktop checks the `Origin` of
each connection, requires a per-surface session credential and binds an
installed browser with a client cookie; in a CS-WebUI window, who can connect
is decided by native WebUI alone: its local server, the token it writes into
its `webui.js` and its single-client default. Treat these settings as security
decisions:

- **`SetPublic(true)`.** `ConfigureWindow` can call it on the `WebUiWindow`. The
  window's server is then reachable from the network, not only from this
  machine, and any client that can load the page can call the Bridge. Leave it
  off unless the application authenticates its users itself.
- **Serve-only mode.** With `RUNIC_APPLICATION_SERVE_ONLY=1` the window writes
  its URL to standard output and waits for a client. The first local client to
  connect gets the window, and on a shared machine that can be another user's
  process. Use it for test harnesses on machines you control.
- **Multiple clients.**
  `WebUiApplication.SetConfiguration(WebUiConfiguration.MultiClient, true)` lets
  every client that reaches the server share the window. `IRunicConnections`
  then lists each client's connection and tells a command which one invoked it
  ([Several clients](#several-clients)), but its keys only tell pages apart:
  authenticate users in the application before you act on who they are.
- **Your own WebUI build.** A library from `CSWEBUI_NATIVE_LIBRARY` or
  `WebUiNativeLibrary.SetLibraryPath` must include WebUI commit `efac0e24d`:
  with an older WebUI, Bridge calls that arrive together can run with each
  other's arguments or receive each other's replies. Runic is tested with
  `24c7d196e`, which CsWebUi 2.5.0-beta.4.10 bundles (see above). Runic cannot
  check a custom build, so the host logs event 2101 when one is configured.

- **Embedded frontends and file handlers.** WebUI turns its authentication
  cookie off for the whole process when any window installs a file handler,
  because it cannot tell what the handler serves. The cookie binds a window to
  the first browser that loaded its `webui.js`; without it, any local client
  can load the window's pages and connect while the window has no client. A
  window that serves an asset source (`Assets`, or
  `RunicApplicationFrontendDelivery=Embedded`) turns the cookie back on after
  installing its handler, so it admits clients as a window that serves a folder
  does. A file handler that the application installs itself, through
  `ConfigureWindow` or `ShowWindow`, leaves the cookie off unless the
  application calls `WebUiApplication.SetConfiguration(WebUiConfiguration.UseCookies, true)`.
- **Content-Security-Policy.** WebUI serves `SetRootFolder` files without
  headers of its own, so give the page a policy with
  `<meta http-equiv="Content-Security-Policy" content="…">`. A `<meta>` policy
  cannot carry `frame-ancestors`, `report-uri` or `sandbox`. The Bridge needs
  `'self'` scripts and connections and no `'unsafe-eval'`, as in
  `default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'`.
  The host sends Views state and navigations as data, which `runic-views.js`
  parses instead of evaluating. WebUI still evaluates script for
  `WebUiWindow.RunJavaScript`/`ExecuteJavaScript` and
  `WebUiEvent.RunJavaScript`, and for `SetSize` or `SetPosition` while a browser
  window is connected (the host sizes a window before it shows it). An
  application that uses them needs `'unsafe-eval'`.

Runic does not review native WebUI's admission. For a page that shows
untrusted content, or a machine shared with other users, prefer Runic Desktop
(`Runic.Application.Views.Desktop`), whose admission Runic implements and
documents.

See the [Runic.Application.Views package guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
for Windows, Views, generated clients, and build properties.
