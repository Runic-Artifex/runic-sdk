## CS-WebUI's own pages and banners in the application's language (cs-webui#32, preview)

CS-WebUI's 404 and 500 pages and the connection banners of WebUI's bridge ("Failed to connect to the backend
application") now take `DesktopPlatformTexts.Current`, which `Runic.Application.Views.Translations` keeps in the
application's language, with the window's locale as their language. `DesktopPlatformTexts` has six new texts for them
(`ServerErrorTitle`, `ServerErrorMessage`, `ConnectionErrorLabel`, `ConnectionFailedMessage`, `ConnectionLostMessage`,
`WebSocketUnsupportedMessage`), and the `runic-views` catalog has them in English and German. An embedded frontend's
missing file now gets a Resource Not Available page instead of an empty `404`. `runic-views.js` loads the banners'
script from the window's `/_runic/cswebui/texts.js`, which the window's file handler answers; a window served from a web
root folder now gets that handler too, with WebUI's authentication cookie kept on.

WebUI's own Access Denied page and its 404 page for a file missing from a web root folder stay English (D061.8): WebUI
sends them itself. See
[Runic's own text](../../../packages/dotnet/Runic.Application.Views.CsWebUi/README.md#runics-own-text).
