# Runic Application Desktop

`Runic.Application.Desktop` hosts generated Runic Windows and Views on a
`Runic.Desktop` surface. The surface owns authenticated browser or embedded
WebView sessions and removable capability bindings; the application Window owns
its ViewModel scope and logical View lifetimes.

Load `webui.js` and `runic-desktop-views.js` before the generated TypeScript
client in the frontend document. The adapter uses the same host-neutral client
contract as the CS-WebUI adapter.
