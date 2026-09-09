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
remain replaceable through `IDesktopWindowHostFactory`. The internal
`webui-compat/52f9e75` implementation is retained only as protocol and
differential evidence; no public `WebUi*` identity is exported.

Call `DesktopHost.GetPresentationPreflight` before opening a window when the
application needs to present actionable platform prerequisites. It evaluates the
requested host and only the explicit fallback policy without creating a browser
or WebView. Sensitive permissions remain denied unless the window opts into a
typed grant. Direct legacy capability failures retain a redacted, correlated
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

Runic applications pass the same options through `DesktopApplicationHostOptions.Host`.
No Linux toolkit is selected by default. Browser-only applications need no GTK
selection. `DesktopPlatform.GetLinuxEmbeddedBackends()` inspects both library sets
without loading either toolkit; `host.GetPresentationPreflight(...)` evaluates the
configured provider. A process cannot change GTK versions after claiming a backend.
`EmbeddedThenBrowser` remains an explicit browser fallback, never a GTK3/GTK4 retry.
GTK4 stays optional; existing first-party applications explicitly retain GTK3.

The optional native dispatcher interface lets application platform services use the
same owner/lifetime checks with custom hosts. Window capabilities are forwarded from
the provider, including operations unavailable under GTK4/Wayland. For native file
pickers and clipboard, register the matching GTK platform provider and the
[portal service](../Runic.Platform.Linux.Portal/README.md).
