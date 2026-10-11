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
- Structured browser/WebView preflight with actionable prerequisite diagnostics
- Sensitive permissions denied by default and explicit, typed presentation opt-in
- Window geometry, framing, transparency, visibility, focus, and native handles
- [Asynchronous native close confirmation](https://docs.runic-artifex.eu/guides/desktop/window-close-lifecycle/) for unsaved work
- Trimming and NativeAOT-compatible managed core

## Example

```csharp
using Runic.Desktop;

await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
{
    // Windows uses WebView2 and macOS WKWebView; Linux selects a toolkit explicitly.
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
});
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = new DesktopContent.Html("""
    <!doctype html>
    <html>
    <head><script src="webui.js"></script></head>
    <body><button onclick="greet('Runic').then(alert)">Greet</button></body>
    </html>
    """),
});
using var greeting = surface.RegisterCapability(
    "greet",
    static (invocation, _) =>
        ValueTask.FromResult<PresentationResult>($"Hello, {invocation.GetString()}!"));
await using var window = await surface.OpenWindowAsync();
await window.WaitForCloseAsync();
```

Run the included sample from source:

```console
dotnet run --project samples/Runic.Desktop.Sample
dotnet run --project samples/Runic.Desktop.Sample -- --webview
```

## Platform WebViews

- Windows uses the Microsoft Edge WebView2 Runtime.
  Windows x64/ARM64 NativeAOT publishes link the WebView2 loader into the
  executable automatically; no adjacent `WebView2Loader.dll` is required.
  The Edge runtime must still be installed. JIT builds retain the native DLL.
- macOS uses the system WebKit framework.
- Linux requires explicit `DesktopHostOptions.Linux.EmbeddedBackend` selection: GTK3/WebKitGTK 4.1, or the optional `Runic.Desktop.Gtk4` provider with GTK4/WebKitGTK 6.0, plus a graphical display.

Applications can provide an `IDesktopWindowHostFactory` in immutable host
options without replacing the managed server, transport, capabilities, or
lifecycle.

Windows open in the embedded WebView unless `DesktopWindowOptions.Browser`
selects an installed browser. Check a window request at startup, before doing
application work:

```csharp
var windowOptions = new DesktopWindowOptions
{
    PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
    Width = 1000,
    Height = 700,
};
var validation = host.Validate(windowOptions);
foreach (var warning in validation.Warnings)
{
    Console.Error.WriteLine($"{warning.Code} ({warning.Option}): {warning.Message}");
}
validation.ThrowIfInvalid();
```

`Validate` reports a missing WebView runtime, browser or Linux toolkit, every
window option the selected host rejects or ignores, and how permission grants
apply, each with a stable code and remediation, without starting a
presentation. It logs the diagnostics through `DesktopHostOptions.LoggerFactory`.
`ThrowIfInvalid` throws a `DesktopConfigurationException` that lists the
errors; warnings name options the presentation ignores. `GetPresentationPreflight`
returns the same checks as typed results.

The default `RequestedOnly` policy never changes presentation mode. Applications
that deliberately prefer a WebView but can continue in a browser can select
`DesktopPresentationPolicy.EmbeddedThenBrowser`; the resulting
`DesktopWindow.FellBack` property and the diagnostic sink make that decision
observable. Camera and microphone access remains denied unless
`DesktopPermissionGrant.MediaCapture` is explicitly selected for the window.
WebView2, WebKitGTK and GTK4 windows grant it only to the presented origin;
WKWebView windows and Firefox ask the user instead, and an explicitly selected
Chromium-based browser accepts capture for every origin it opens. A browser
fallback opens without the grant.

The retained `webui-compat/52f9e75` direct-capability profile cannot carry a
structured invocation failure on its legacy wire response. It reports only the
stable empty compatibility result while the host emits a redacted,
correlation-bearing diagnostic. Runic Application Views hosted through
`Runic.Application.Views.Desktop` report their own typed, redacted, correlation-bearing errors.

## Troubleshooting

Run `dotnet runic doctor` in the project, with `--rid` for a target machine,
to list the native runtime it needs. At startup, `host.Validate(windowOptions)`
reports each missing prerequisite with a stable code and remediation:

- **`webview2-runtime-missing` (Windows).** Install the Microsoft Edge WebView2
  Runtime on the machine. NativeAOT publishes need no `WebView2Loader.dll`.
- **`linux-embedded-backend-not-selected`.** Set
  `DesktopHostOptions.Linux.EmbeddedBackend`, as in the example above. No
  toolkit is selected by default.
- **`webkitgtk-runtime-missing`, `gtk4-runtime-missing` or
  `webkitgtk6-runtime-missing` (Linux).** Install GTK 3 and WebKitGTK 4.1, or
  GTK 4.12 or newer and WebKitGTK 6.0 for `Runic.Desktop.Gtk4`, and run in a
  graphical session.
- **`gtk4-provider-missing`.** A GTK4 backend needs the `Runic.Desktop.Gtk4`
  package and its main-thread entry point.
- **GTK 3 loaded in a GTK 4 process.** A process cannot change GTK versions
  after claiming a backend, and a GTK 4 process must not load GTK 3. Replace
  `Runic.Platform.Linux` with `Runic.Platform.Linux.Gtk4` in a GTK4
  application; doctor's `gtk4-profile` check lists what is missing.

## Relationship to WebUI and CS-WebUI

Runic Desktop began as a behavioral port of WebUI. WebUI remains a compatibility
oracle for its established window, bridge, binding, content, and lifecycle
behavior, while Runic Desktop owns its implementation and public API.

[CS-WebUI](https://github.com/Runic-Artifex/cs-webui) remains the independently
maintained .NET binding for unmodified upstream WebUI. Runic Desktop has no
production dependency on CS-WebUI or the WebUI native library.

The internal WebUI-profile engine remains differential evidence; it is not part
of the public API. Existing source-preview consumers can use the
[migration guide](https://docs.runic-artifex.eu/guides/desktop/migrations/webui-compat-to-desktop/).
The [wire profile](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/wire-profile.md) documents the
compatibility boundary.

## Product contract

Runic Desktop implements the language-neutral
[Runic Desktop presentation contract](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/README.md). The contract defines
host, surface, window, session, request, streaming, cancellation, security, and
error semantics independently of .NET and TypeScript APIs. Its
[ownership map](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/ownership.md) keeps Runic Application Views, Assets,
Translations, Vite, and framework responsibilities with their existing Runic
products.

## License and attribution

Runic Desktop is MIT licensed. Its managed bridge implementation is informed by
WebUI's MIT-licensed wire protocol and TypeScript bridge. The upstream license
is retained in `eng/licenses/WebUI-LICENSE.txt` and the relevant attribution is
recorded in `NOTICE`.

### Minimal hosting profile

Set `RunicDesktopMinimalHost=true` to use the opt-in empty ASP.NET Core builder
with explicit Kestrel core and socket transport. A NativeAOT linker feature switch
removes the default slim-builder path. Runic's surface, transport and admission
behavior is retained; default configuration providers are omitted. Test any custom
service assumptions. See the SDK's [size and tuning guide](https://docs.runic-artifex.eu/guides/desktop/size-and-tuning/).
