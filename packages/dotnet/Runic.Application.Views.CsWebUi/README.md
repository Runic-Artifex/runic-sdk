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
services.AddRunicViews(); // generated in <ProjectName>.RunicBridgeComposition
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
`CsWebUiBridgeWindow<TViewModel>`; `NativeWindow` exposes the underlying
`WebUiWindow`. `WaitForCloseAsync` completes when WebUI reports that its
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
`BridgeWindowCloseResult`, and disposal is asynchronous only. Accepted
operations can keep the Window's DI scope alive after the visible window
closes, so dispose the Window with `await using` before the application returns.

State snapshots and collection delta frames are written, in order, as WebSocket
messages to every connected client. Runic Desktop waits until the WebView has run
each frame. CS-WebUI cannot do that for several clients, so its writes block
instead, under WebUI's process-wide send lock, until each socket accepts the frame or
the write times out. A slow browser therefore holds back delivery at the TCP level.
Frames queue in the bridge, which sends a recovery snapshot once the queue is full,
and sends to every other CS-WebUI window in the process wait for the same lock.

## What a window opens

By default a window uses WebUI's recommended presentation: an installed browser
in app mode (Chrome, Edge and other Chromium-based browsers work best; Firefox
works without app mode), then the system default browser, then the platform
WebView. `new RunicCsWebUiHost(provider) { UseWebView = true }` always uses the
WebView: the Edge WebView2 Runtime on Windows, GTK 3 with WebKitGTK 4.1 on
Linux, or WKWebView on macOS. `ConfigureWindow` configures each `WebUiWindow`
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
owner, so every operation and the capability snapshot report
`Unavailable(OwnerUnavailable)`. It also registers one `IDesktopSettings`,
`IDesktopNotifications`, `IDesktopStatusIcon`, `IDesktopLoginStartup` and
`IDesktopInhibition`, the same application services as Runic Desktop, whose
requests return `Unavailable(ProviderNotConfigured)`: this package selects no
native provider. They need no window, so register a platform provider's service,
such as `WindowsPlatformProvider.CreateSettings()` or
`new PortalApplication("org.example.Notes").CreateUnparentedInhibition()`, before
it to use one on CS-WebUI. The
[Runic.Platform guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#hosts-without-a-native-owner)
lists each service's result on both hosts. A root ViewModel that implements `IRunicFileDropTarget` is
never called here: a browser window reports no dropped paths, so the page only
gets HTML5 `File` objects. Likewise there is no native menu bar: an `IRunicMenuCommandSource`
is never asked, so render menus in the page, bound to the same commands.

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

Set `RUNIC_APPLICATION_SERVE_ONLY=1` to start the local server without opening
anything. Each window then writes `RUNIC_APPLICATION_URL=<url>` to
standard output, ignoring `UseWebView` and `ShowWindow`, and keeps serving
until a line is read from standard input or the process ends, so a test
harness or remote browser can drive the unchanged application. Write the line
on Windows too; the
[serve-only protocol](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md#automation)
is the same as on Runic Desktop, and `RunicServeOnlyApplication` in
[Runic.Application.Testing](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md#the-whole-application-in-a-browser)
implements it for browser tests. `ValidateWindow` has no presentation checks on
CS-WebUI. On Linux with GTK 3 and WebKitGTK 4.1 installed, WebUI initializes
GTK when it creates a window, also in serve-only mode, and GTK ends the process
when no display opens. Give a headless run a display, for example with
`xvfb-run`.

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

Native WebUI before the build that CsWebUi 2.5.0-beta.4.7 bundles could assign
one event slot to two calls that arrive together, and one of them then never
received a reply (webui-dev/webui#721). The script therefore sends the next
Bridge call only after .NET reports the previous one as received, or after it
settles, which also covers an older WebUI loaded through
`CSWEBUI_NATIVE_LIBRARY`. Concurrent calls are sent one server round trip apart. Applications
that keep their own copy of `runic-cswebui.js` should update it.

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
returns `disconnected`. CS-WebUI cannot remove a native route registration
before its window closes. Retained page references reuse routes; new page
identities still add routes.

See the [Runic.Application.Views package guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
for Windows, Views, generated clients, and build properties.
