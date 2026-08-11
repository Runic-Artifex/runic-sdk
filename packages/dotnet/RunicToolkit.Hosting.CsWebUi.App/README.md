# RunicToolkit.Hosting.CsWebUi.App

This package projects native CS-WebUI application composition onto the shared
`WebUiAppBuilder`. The low-level CS-WebUI adapter remains independent of Generic
Host; applications opt into this package when they want the high-level surface.

```csharp
var builder = WebUiApp.CreateBuilder(args);
builder.UseCsWebUi(
    "MyFrontend",
    new CsWebUiAppOptions(
        assets,
        root,
        new CsWebUiAdapterOptions(webRoot, configureWindow: root.ConfigureWindow),
        new BrowserHostOptions("my-app"),
        new BrowserWindowOptions("main", "My app", 1000, 720)));

return await builder.RunAsync();
```

Framework integrations normally wrap `UseCsWebUi` with their own extension
members, such as `UseReact`. Toolkit hosting stays renderer-neutral so frontend
integrations do not acquire unrelated runtime dependencies.

Registration also contributes the frontend-neutral `RunicToolkit.Desktop`
services. Applications can inject typed lifetime, window, focus, dispatcher,
keyboard, clipboard, file, drop, external-launch, notification, browser
profile/storage, and owned-window contracts without referencing CS-WebUI or DOM
types. The services attach to the exact native window lifetime owned by the
mode runner and release secondary windows before the browser host.
