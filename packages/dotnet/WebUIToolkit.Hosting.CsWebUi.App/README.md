# WebUIToolkit.Hosting.CsWebUi.App

This package projects native CsWebUi application composition onto the shared
`WebUiAppBuilder`. The low-level CsWebUi adapter remains independent of Generic
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
members, such as `UseReact`. The package directly supplies
`CwhtmlHtmx`/`UseCwhtmlHtmx` because that composition requires no dependency
on the host-neutral HTMX runtime.
