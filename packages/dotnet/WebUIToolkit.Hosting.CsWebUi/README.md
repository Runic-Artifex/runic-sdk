# WebUIToolkit.Hosting.CsWebUi

This package adapts [CsWebUi](https://github.com/ViktorJannicke/cs-webui) to the
browser-neutral contracts in `WebUIToolkit.Hosting.Abstractions`. It serves a
local frontend directory without ASP.NET Core and supports CsWebUi's automatic
browser selection, an explicitly selected installed browser, or an embedded
WebView.

The adapter remains prerelease while its upstream `CsWebUi` dependency is
prerelease.

```csharp
using CsWebUi;
using WebUIToolkit.Hosting.CsWebUi;

var factory = new CsWebUiBrowserHostFactory(
    new CsWebUiAdapterOptions(
        "wwwroot",
        CsWebUiPresentationMode.Auto,
        configureWindow: window =>
        {
            window.Bind("greet", static webEvent =>
                WebUiResult.FromString($"Hello, {webEvent.GetString()}!"));
        }));
```

Pass `factory` to `WebUiModeRunner`. The runner supplies an `app://` entry URI;
the adapter accepts only the current application host, rejects credentials,
ports, queries, fragments, traversal, encoded separators, and control
characters, and translates the URI to a relative CsWebUi root path.

`BrowserWindowOptions` size and resizability are applied before show. The title
is set after CsWebUi establishes the client connection. The server is kept
private to the local machine. A disconnected client raises `CloseRequested`
and completes `WaitForCloseAsync`.

The native configuration callback runs after `WebUiWindow` creation and before
the window can be shown. It is intended for CsWebUi `Bind`/`BindAsync`
registration; do not show or dispose the window from that callback.

CsWebUi owns process-wide native state, so adapter dispatchers serialize native
work across hosts. Disposing a host closes only its owned windows; it does not
call the process-wide `WebUiApplication.Exit` or `Clean` methods.

Applications that want the shared high-level builder add the separate
`WebUIToolkit.Hosting.CsWebUi.App` composition package. Keeping that dependency
out of this adapter allows custom hosts to use CsWebUi without Generic Host.
