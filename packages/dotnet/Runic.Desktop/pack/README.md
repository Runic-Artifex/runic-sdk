# Runic Desktop

Runic Desktop is a managed .NET runtime for web-powered desktop applications.
It serves application content with ASP.NET Core, connects JavaScript and .NET,
and opens the interface in an installed browser or an embedded platform
WebView. It does not load the native WebUI library.

> **Source preview · first package pending.** The API may change before the
> first published preview.

## Capabilities

- Embedded HTML, files, folders, external URLs, and virtual content
- Synchronous and asynchronous JavaScript-to-.NET bindings
- Managed-to-JavaScript execution, navigation, and raw byte transport
- Kestrel HTTP and WebSocket hosting with loopback or public binding
- Installed-browser discovery, isolated profiles, kiosk mode, and process ownership
- Embedded WebView2, WKWebView, and WebKitGTK windows
- Window geometry, framing, transparency, visibility, focus, and native handles
- Trimming and NativeAOT-compatible managed core

## Example

```csharp
using Runic.Desktop;

using var window = new WebUiWindow();
window.Bind("greet", name => $"Hello, {name.GetString()}!");
window.Show("""
    <!doctype html>
    <html>
    <head><script src="webui.js"></script></head>
    <body><button onclick="greet('Runic').then(alert)">Greet</button></body>
    </html>
    """);

WebUiApplication.Wait();
```

Run the included sample from source:

```console
dotnet run --project samples/Runic.Desktop.Sample
dotnet run --project samples/Runic.Desktop.Sample -- --webview
```

## Platform WebViews

- Windows uses the Microsoft Edge WebView2 Runtime.
- macOS uses the system WebKit framework.
- Linux requires GTK 3, WebKitGTK 4.1 or 4.0, and a graphical display.

Applications can replace the embedded platform adapter through
`WebUiApplication.SetEmbeddedHostFactory` without replacing the managed server,
bridge, bindings, or lifecycle.

## Relationship to WebUI and CS-WebUI

Runic Desktop began as a behavioral port of WebUI. WebUI remains a compatibility
oracle for its established window, bridge, binding, content, and lifecycle
behavior, while Runic Desktop owns its implementation and future API direction.

[CS-WebUI](https://github.com/Runic-Artifex/cs-webui) remains the independently
maintained .NET binding for unmodified upstream WebUI. Runic Desktop has no
production dependency on CS-WebUI or the WebUI native library.

The current compatibility work and intentional differences are recorded in the
[roadmap](docs/design/runic-desktop-roadmap.md).

## License and attribution

Runic Desktop is MIT licensed. Its managed bridge implementation is informed by
WebUI's MIT-licensed wire protocol and TypeScript bridge. The upstream license
is retained in `eng/licenses/WebUI-LICENSE.txt` and the relevant attribution is
recorded in `NOTICE`.
