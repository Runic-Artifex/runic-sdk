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
