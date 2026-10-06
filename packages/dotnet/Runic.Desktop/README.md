# Runic.Desktop

`Runic.Desktop` is the managed .NET implementation of the Runic Desktop
presentation contract. Its public API models listener ownership with
`DesktopHost`, isolated content and transport namespaces with `DesktopSurface`,
and optional browser or embedded-WebView presentations with `DesktopWindow`.

The host defaults to loopback binding. Each surface defaults to same-origin
browser admission, a cryptographically random 256-bit session credential,
single-client admission, scoped-root content access, redacted failures, and
non-cacheable bootstrap content. Additional origins, multiple clients, missing
browser origins, or non-loopback exposure are explicit immutable options.

Content handlers receive request-scoped services and may return fixed or
request-owned streaming responses. Stream writes are awaited for backpressure;
requester disconnect, surface close, and host shutdown propagate cancellation
and release the body exactly once. Multiple surfaces share one listener by
default, while `UseIsolatedListener` is available for policy boundaries.

Installed browsers and the built-in WebView2, WKWebView, and WebKitGTK adapters
remain replaceable through `IDesktopWindowHostFactory`. A host reporting
`SupportsDocumentStartScript` must run `DesktopWindowHostOptions.DocumentStartScript`
in every frame before page scripts; the surface then withholds session
credentials from its fetchable bootstrap scripts. The internal
`webui-compat/52f9e75` implementation is retained only as protocol and
differential evidence; no public `WebUi*` identity is exported.

`DesktopWindowOptions.Browser` defaults to `BrowserKind.Embedded`, the platform
WebView; installed browsers are an explicit choice or an explicit
`EmbeddedThenBrowser` fallback.

Call `DesktopHost.Validate(windowOptions)` at startup to check a window request
before anything opens. It evaluates the requested host and only the explicit
fallback policy without creating a browser or WebView, and returns one
`DesktopDiagnostic` per problem, each with a stable `Code`, the `Option` it
concerns, a `Remediation`, and a `Severity`. Errors make the request fail when it
opens; warnings mean the presentation ignores or narrows an option.
`ThrowIfInvalid()` throws a `DesktopConfigurationException` listing every error,
and each diagnostic is logged through `DesktopHostOptions.LoggerFactory`.
`GetPresentationPreflight` returns the same checks as `Diagnostic` and
`OptionDiagnostics` for applications that present them themselves.

| Code | Severity | Reported when |
| --- | --- | --- |
| `linux-embedded-backend-not-selected`, `gtk4-provider-missing`, `webkitgtk-runtime-missing`, `webview2-runtime-missing`, `browser-not-found`, … | Error | The presentation or a native prerequisite is unavailable (`DesktopPlatform.GetAvailability()` lists them). |
| `browser-unsupported` | Error | `Browser` is Safari or Opera, which Runic Desktop cannot launch. |
| `window-option-invalid` | Error | An option has an undefined value, or `ConfirmCloseAsync` is set for a browser presentation. |
| `window-option-unsupported` | Error or Warning | The window host rejects an option (Error, such as GTK4 placement) or the presentation ignores it (Warning, such as `Frameless` in a browser). |
| `window-option-incomplete` | Warning | Only one of `X`/`Y` or `MinimumWidth`/`MinimumHeight` is set. |
| `permission-grant-unsupported` | Warning | The presentation does not apply `MediaCapture` and asks the user instead (WKWebView, Firefox). |
| `permission-grant-withheld` | Warning | An `EmbeddedThenBrowser` fallback opens without the grant. |

Sensitive permissions remain denied unless the window opts into a typed grant.
WebView2, WebKitGTK and GTK4 windows grant `MediaCapture` only to the presented
origin. A browser cannot limit a grant to one origin, so a browser fallback opens
without it and reports `permission-grant-withheld` to the diagnostic sink and the
logger; an explicitly selected Chromium-based browser accepts capture for every
origin it opens. A custom `IDesktopWindowHostFactory` reports the options it
rejects or ignores through `ValidateOptions`. Direct legacy capability failures retain a redacted, correlated
host diagnostic; the TypeScript transport maps its own frontend failures to
typed correlation-bearing errors.

Linux embedded hosting requires an explicit selection before opening a window:

```csharp
var options = new DesktopHostOptions
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
};
await using var host = await DesktopHost.StartAsync(options);
```

GTK4 also requires the main-thread entry point in
[Runic.Desktop.Gtk4](../Runic.Desktop.Gtk4/README.md), along with
`Gtk4WebKit6` and `Gtk4WindowHostFactory`. Follow that provider's complete
startup example before introducing a top-level `await`.

Runic Windows and Views pass the same `DesktopHostOptions` to `DesktopHost.StartAsync`
before `OpenDesktopWindowAsync` from `Runic.Application.Desktop`.
No Linux toolkit is selected by default. Browser-only applications need no GTK
selection. `DesktopPlatform.GetLinuxEmbeddedBackends()` inspects both library sets
without loading either toolkit; `host.GetPresentationPreflight(...)` evaluates the
configured provider. A process cannot change GTK versions after claiming a backend.
`EmbeddedThenBrowser` remains an explicit browser fallback, never a GTK3/GTK4 retry.
It falls back when the embedded presentation cannot start, not when it opens but its
page never connects: that fails with `presentation-connection-timeout`, and the inner
`TimeoutException` says how far the page got.
GTK4 stays optional; existing first-party applications explicitly retain GTK3.

The optional native dispatcher interface lets application platform services use the
same owner/lifetime checks with custom hosts. Window capabilities are forwarded from
the provider, including operations unavailable under GTK4/Wayland. For native file
pickers and clipboard, register the matching GTK platform provider and the
[portal service](../Runic.Platform.Linux.Portal/README.md).
