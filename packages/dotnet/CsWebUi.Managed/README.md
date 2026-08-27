# CS-WebUI Managed

`CsWebUi.Managed` is the managed-first CS-WebUI engine. It is being developed
as a behavioral port of WebUI that uses Kestrel for HTTP and WebSocket hosting
and does not load the WebUI native library.

The preview supports embedded HTML, explicit files, folder roots, virtual
content, external URLs, browser launching, synchronous or asynchronous
JavaScript-to-.NET bindings, WebUI lifecycle and click events,
managed-to-JavaScript execution, navigation, and raw byte transport. Its
generated bridge implements WebUI's binary token handshake, binding discovery,
function calls, correlated responses, large `MULTI` packets, keepalive, and
reconnect behavior.

M3 browser-host compatibility is complete. The server supports WebUI's
index discovery and redirects, MIME and no-cache behavior, explicit ports,
loopback or public binding, cookie-backed client ownership, single- or
multi-client operation, deterministic restart, and application-wide wait and
exit. Browser hosting adds discovery and explicit selection, Chromium app mode,
isolated or user-supplied profiles, custom arguments, proxy and kiosk options,
window geometry, process-tree ownership, close detection, and generated-profile
cleanup. Physical files are streamed by Kestrel; virtual handlers return
complete in-memory responses until the managed streaming API is introduced in
M5.

Embedded WebViews remain the next milestone, and the preview API is not yet
compatibility-stable.
