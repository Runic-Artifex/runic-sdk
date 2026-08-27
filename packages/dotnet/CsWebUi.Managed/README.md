# CS-WebUI Managed

`CsWebUi.Managed` is the managed-first CS-WebUI engine. It is being developed
as a behavioral port of WebUI that uses Kestrel for HTTP and WebSocket hosting
and does not load the WebUI native library.

The preview supports embedded HTML, browser launching, synchronous or
asynchronous JavaScript-to-.NET bindings, WebUI lifecycle and click events,
managed-to-JavaScript execution, navigation, and raw byte transport. Its
generated bridge implements WebUI's binary token handshake, binding discovery,
function calls, correlated responses, large `MULTI` packets, keepalive, and
reconnect behavior.

M1 bridge and binding compatibility is complete. Content routing, browser host
selection, multi-client policy, and embedded WebViews remain later milestones,
and the preview API is not yet compatibility-stable.
