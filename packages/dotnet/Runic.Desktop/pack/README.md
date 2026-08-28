# Runic Desktop

Runic Desktop is a managed .NET runtime for web-powered desktop applications.
It serves application content with ASP.NET Core, connects JavaScript and .NET,
and opens the interface in an installed browser or an embedded platform
WebView. It does not load the native WebUI library.

> **Source preview · first package pending.** The API may change before the
> first published preview.

## Capabilities

- Embedded HTML, files, folders, external URLs, and fixed or streaming virtual content
- Async JavaScript-to-.NET presentation capabilities
- Managed-to-JavaScript execution, navigation, and raw byte transport
- Shared or isolated Kestrel listeners with explicit host and surface ownership
- Request-scoped dependency injection, streaming backpressure, and cancellation causes
- Same-origin admission and 256-bit surface-scoped session credentials by default
- Installed-browser discovery, isolated profiles, kiosk mode, and process ownership
- Embedded WebView2, WKWebView, and WebKitGTK windows
- Window geometry, framing, transparency, visibility, focus, and native handles
- Trimming and NativeAOT-compatible managed core

## Example

```csharp
using Runic.Desktop;

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = """
    <!doctype html>
    <html>
    <head><script src="webui.js"></script></head>
    <body><button onclick="greet('Runic').then(alert)">Greet</button></body>
    </html>
    """,
});
using var greeting = surface.RegisterCapability(
    "greet",
    static (invocation, _) =>
        ValueTask.FromResult<PresentationResult>($"Hello, {invocation.GetString()}!"));
await using var window = await surface.OpenWindowAsync();
window.WaitForClose();
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

Applications can provide an `IDesktopWindowHostFactory` in immutable host
options without replacing the managed server, transport, capabilities, or
lifecycle.

## Relationship to WebUI and CS-WebUI

Runic Desktop began as a behavioral port of WebUI. WebUI remains a compatibility
oracle for its established window, bridge, binding, content, and lifecycle
behavior, while Runic Desktop owns its implementation and future API direction.

[CS-WebUI](https://github.com/Runic-Artifex/cs-webui) remains the independently
maintained .NET binding for unmodified upstream WebUI. Runic Desktop has no
production dependency on CS-WebUI or the WebUI native library.

The internal WebUI-profile engine remains differential evidence; it is not part
of the public API. Existing source-preview consumers can use the
[M6 migration guide](docs/migrations/webui-compat-to-desktop.md). Compatibility
work and intentional differences are recorded in the
[roadmap](docs/design/runic-desktop-roadmap.md).

## Product contract

M5 and later implement the language-neutral
[Runic Desktop presentation contract](contract/README.md). The contract defines
host, surface, window, session, request, streaming, cancellation, security, and
error semantics independently of .NET and TypeScript APIs. Its
[ownership map](contract/ownership.md) keeps Application Bridge, Assets,
Translations, Vite, and framework responsibilities with their existing Runic
products.

## License and attribution

Runic Desktop is MIT licensed. Its managed bridge implementation is informed by
WebUI's MIT-licensed wire protocol and TypeScript bridge. The upstream license
is retained in `eng/licenses/WebUI-LICENSE.txt` and the relevant attribution is
recorded in `NOTICE`.
