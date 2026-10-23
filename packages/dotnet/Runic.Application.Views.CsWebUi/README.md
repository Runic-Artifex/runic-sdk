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
`WebUiWindow`; `CsWebUiPlatformServices.GetNativeWindow(window)` returns it for
any Window of this host without naming the ViewModel type, and `null` for
another host's. `WaitForCloseAsync` completes when WebUI reports that its
windows have closed: CS-WebUI reports closing for all of its windows at once,
so with several windows open it waits for all of them.

A browser window closes without asking the application, and WebUI reports the
close only after it happened, so CS-WebUI cannot keep a window open. A Window
with `GuardClose` opens unguarded, logs event 1053 `WindowCloseGuardUnavailable`
once, and its `CanCloseAsync` is not asked when the user closes the browser
window; the navigation regions' departure guards still run for navigation
inside the window. `window.RequestCloseAsync()` still asks `CanCloseAsync`
before it closes the window, because the application is the one closing. Use
Runic Desktop's embedded window when a user close must be able to keep unsaved
work open.

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

State snapshots and collection delta frames are written, in order, as WebSocket
messages to every connected client. Runic Desktop waits until the WebView has run
each frame. CS-WebUI cannot do that for several clients, so its writes block
instead until each socket accepts the frame or the write times out. A slow
browser therefore holds back delivery at the TCP level. Frames queue in the
bridge, which sends a recovery snapshot once the queue is full, and the window's
other clients wait for the same write. Other CS-WebUI windows are not held back:
WebUI before nightly `9264f7ae9` (webui-dev/webui#723) wrote every WebSocket
frame under one process-wide lock, so one stalled client delayed every window in
the process by up to 30 seconds per frame. CsWebUi 2.5.0-beta.4.8, the lowest
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
`WEBUI_BROWSER_PATH`. For native
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
lists each service's result on both hosts. A root ViewModel that implements `IRunicFileDropTarget` is
never called here: a browser window reports no dropped paths, so the page only
gets HTML5 `File` objects. There is no native menu bar either: the page renders
`RunicCsWebUiHostOptions.MenuBar` (see [Menu bar](#menu-bar)).

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
anything. Each window then writes `RUNIC_APPLICATION_URL=<url>` to
standard output, ignoring `UseWebView` and `ShowWindow`, and keeps serving
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
The routes are those of Runic Desktop
([specs/desktop/menu-bar-page.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/menu-bar-page.md)),
from the same source, so the same frontend runs on both hosts:

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

The package's build targets copy `runic-cswebui.js` into `www/` next to the
built frontend, also as `runic-views.js`. Load `webui.js` (served by WebUI) and
`runic-views.js` before the generated client in `index.html`; the same page then
runs on Runic Desktop, whose package copies its own client as `runic-views.js`.
A project that references both hosts sets `RunicApplicationHost` (`cswebui` or
`desktop`); see
[the host of a page](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#the-host-of-a-page).
Pages that load `runic-cswebui.js` keep working. The script adapts
`window.webui` to the generated client's transport contract.

The script sends each Bridge call as soon as the page makes it, so several
calls can reach WebUI at once. Runic relies on WebUI's fix for concurrent calls
(webui-dev/webui#721): before it, native WebUI could assign one event slot to
two calls that arrive together. One call could then run with the other's
arguments or receive the other's reply, and one of them could receive no reply
at all. The fix is in WebUI nightly `efac0e24d` and in every CsWebUi since
2.5.0-beta.4.7. CsWebUi 2.5.0-beta.4.8, the lowest version this package
accepts, bundles WebUI nightly `2b35db13f864031543f61816c496b1035c026340`,
which also removes the process-wide send lock (webui-dev/webui#723) and gives
navigation events their URL argument (webui-dev/webui#724). An application that
loads its own WebUI through `CSWEBUI_NATIVE_LIBRARY` or
`WebUiNativeLibrary.SetLibraryPath` needs `efac0e24d` or later for concurrent
calls, and `2b35db13f` or later for the behaviour this package is tested with.
WebUI reports no version or commit that Runic could check, so the host logs
event 2101 `CsWebUiNativeLibraryUnchecked` (Warning) once, before the first
window opens, when such a library is configured.
Applications that keep their own copy of `runic-cswebui.js` should update it.
Since 0.11.0 the host sends Views state to the script's `__runicViewsPush`
function and carries out navigations through `__runicContinueNavigation`, both
as WebUI raw data. A copy from before 0.11.0 has no `__runicViewsPush`: WebUI
then tries to call it as a script, which fails, and the page's Views receive no
state pushes (a Content-Security-Policy reports the refusal). The build
replaces the copy in `www/` on every build, so only a copy the application
keeps elsewhere, such as in a frontend's `public/` folder, can be stale.

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

A window-local `CreateBridgeSession()` retains one native binding for each route
name and swaps the active managed handler as Views change; an inactive route
returns the reply error `released` (Views protocol 3). CS-WebUI cannot remove a native route registration
before its window closes. Retained page references reuse routes; new page
identities still add routes.

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
  every client that reaches the server share the window.
- **Your own WebUI build.** A library from `CSWEBUI_NATIVE_LIBRARY` or
  `WebUiNativeLibrary.SetLibraryPath` must include WebUI commit `efac0e24d`:
  with an older WebUI, Bridge calls that arrive together can run with each
  other's arguments or receive each other's replies. Runic is tested with
  `2b35db13f`, which CsWebUi 2.5.0-beta.4.8 bundles (see above). Runic cannot
  check a custom build, so the host logs event 2101 when one is configured.

- **Content-Security-Policy.** WebUI serves `SetRootFolder` files without
  headers of its own, so give the page a policy with
  `<meta http-equiv="Content-Security-Policy" content="…">`. A `<meta>` policy
  cannot carry `frame-ancestors`, `report-uri` or `sandbox`. The Bridge needs
  `'self'` scripts and connections and no `'unsafe-eval'`, as in
  `default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'`.
  The host sends Views state and navigations as data, which `runic-cswebui.js`
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
