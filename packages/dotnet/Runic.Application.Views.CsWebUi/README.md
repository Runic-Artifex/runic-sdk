# Runic.Application.CsWebUi

Present Runic Windows and Views with [CS-WebUI](https://github.com/Runic-Artifex/cs-webui).
This is the default host of the `runic-app` project template.

```sh
dotnet add package Runic.Application.CsWebUi --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

```csharp
public sealed partial class MainWindow(CsWebUiBridgeWindow<MainViewModel> host)
    : CsWebUiWindow<MainViewModel>(host);

var services = new ServiceCollection();
services.AddScoped<MainViewModel>();
services.AddRunicViews(); // generated in <ProjectName>.RunicBridgeComposition
using var provider = services.BuildServiceProvider();
provider.ValidateWindow<MainViewModel>(); // stops at startup when the Bridge is not registered
await using (var window = provider.OpenWindow<MainWindow, MainViewModel>(host => new MainWindow(host)))
{
    window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();
```

`OpenWindow` creates a DI scope, resolves the Window's ViewModel, constructs
the Window, and attaches its generated Bridge; the Window owns the native
window, scope, and attachments until it is disposed or `CloseAsync` drains its
accepted operations. `CsWebUiWindow<TViewModel>` forwards `SetRootFolder`,
`SetSize`, `Show`, `ShowWebView`, `StartServer`, `CloseAsync`, and `DisposeAsync`
to its `Host`, a `CsWebUiBridgeWindow<TViewModel>`; `Host.NativeWindow` exposes
the underlying `WebUiWindow`.

`ValidateWindow<TViewModel>` checks at startup that the generated Bridge for
the ViewModel is registered. It returns nothing and throws on failure, unlike
Runic Desktop's `ValidateDesktopWindow`, which returns a result to inspect,
because the Bridge check is the only one and always fails the Window.
`OpenWindow` repeats the check before it creates the native window. A missing
`AddRunicViews()` throws a `CsWebUiConfigurationException` (an
`InvalidOperationException`) with the code `bridge-not-registered`, the same
code Runic Desktop reports, and a remediation, instead of a
dependency-injection error. It is logged as event 1050 through the provider's
`ILoggerFactory`. `ValidateWindow` asks the container through
`IServiceProviderIsService`; a container without it is checked only when
`OpenWindow` resolves the Bridge.

Both hosts share one lifetime contract, `IBridgeWindow` from
`Runic.Application.Views`: `CloseAsync(timeout)` returns a `BridgeWindowCloseResult`,
and disposal is asynchronous only. Accepted operations can keep the Window's DI
scope alive after the visible window closes, so dispose the Window with
`await using` before `WebUiApplication.Clean()`.

State snapshots and collection delta frames are written, in order, as WebSocket
messages to every connected client. Runic Desktop waits until the WebView has run
each frame. CS-WebUI cannot do that for several clients, so its writes block
instead, under WebUI's process-wide send lock, until each socket accepts the frame or
the write times out. A slow browser therefore holds back delivery at the TCP level.
Frames queue in the bridge, which sends a recovery snapshot once the queue is full,
and sends to every other CS-WebUI window in the process wait for the same lock.

## What Show opens

`Show` uses WebUI's recommended presentation: an installed browser in app mode
(Chrome, Edge and other Chromium-based browsers work best; Firefox works without
app mode), then the system default browser, then the platform WebView.
`ShowWebView` always uses the WebView: the Edge WebView2 Runtime on Windows,
GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS. For native windows
and platform services, use
[Runic.Application.Desktop](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Desktop/README.md)
instead.

Set `RUNIC_APPLICATION_SERVE_ONLY=1` to start the local server without opening
anything. `Show` and `ShowWebView` then write `RUNIC_APPLICATION_URL=<url>` to
standard output and keep serving until a line is read from standard input or
the process ends, so a test harness or remote browser can drive the unchanged
application.

## Build output

The package's build targets copy `runic-cswebui.js` into `www/` next to the
built frontend. Load `webui.js` (served by WebUI) and `runic-cswebui.js` before
the generated client in `index.html`. The script adapts `window.webui` to the
generated client's transport contract.

Native WebUI can assign one event slot to two calls that arrive together, and
one of them then never receives a reply. The script therefore sends the next
Bridge call only after .NET reports the previous one as received, or after it
settles. Concurrent calls are sent one server round trip apart. Applications
that keep their own copy of `runic-cswebui.js` should update it.

The window binds all WebUI events because only that binding delivers
disconnects. The script stops WebUI from also sending a click event for every
element with an id: those clicks would reach the server in parallel with the
Bridge calls they start. Use Bridge commands rather than WebUI element bindings
in a Views window.

A window-local `CreateBridgeSession()` retains one native binding for each route
name and swaps the active managed handler as Views change; an inactive route
returns `disconnected`. CS-WebUI cannot remove a native route registration
before its window closes. Retained page references reuse routes; new page
identities still add routes.

See the [Runic.Application package guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
for Windows, Views, generated clients, and build properties.
