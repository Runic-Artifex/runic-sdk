# Runic Desktop

`Runic.Desktop` is an independent managed .NET runtime for web-powered desktop
applications. It began as a behavioral port of WebUI, uses Kestrel for HTTP and
WebSocket hosting, and does not load the WebUI native library.

The preview supports embedded HTML, explicit files, folder roots, virtual
content, external URLs, browser launching, synchronous or asynchronous
JavaScript-to-.NET bindings, WebUI lifecycle and click events,
managed-to-JavaScript execution, navigation, and raw byte transport. Its
generated bridge implements WebUI's binary token handshake, binding discovery,
function calls, correlated responses, large `MULTI` packets, keepalive, and
reconnect behavior.

M4 embedded-host compatibility is complete. The server supports WebUI's
index discovery and redirects, MIME and no-cache behavior, explicit ports,
loopback or public binding, cookie-backed client ownership, single- or
multi-client operation, deterministic restart, and application-wide wait and
exit. Browser hosting adds discovery and explicit selection, Chromium app mode,
isolated or user-supplied profiles, custom arguments, proxy and kiosk options,
window geometry, process-tree ownership, close detection, and generated-profile
cleanup. Physical files are streamed by Kestrel; virtual handlers return
complete in-memory responses until the managed streaming API is introduced in
M5.

`ShowWebView` and `ShowWebViewAsync` use WebView2 on Windows, WKWebView on
macOS, and WebKitGTK on Linux. Embedded windows support initial and live
geometry, visibility, focus, minimize/maximize, minimum sizes, framing,
transparency, kiosk mode, icons, platform close events, and native handles.
Frameless pages can mark drag regions with
`--webui-app-region: drag`. Applications can replace the platform adapter with
`WebUiApplication.SetEmbeddedHostFactory` while retaining the managed server,
bridge, bindings, and lifecycle.

Windows requires the Microsoft Edge WebView2 Runtime. macOS uses the system
WebKit framework. Linux requires GTK 3 and WebKitGTK 4.1 or 4.0; a graphical
display is also required. The preview API is not yet compatibility-stable.
