# WebUIToolkit.Hosting.CsWebUi.App public API

All types use the `WebUIToolkit.Hosting.CsWebUi` namespace.

```csharp
public sealed record CsWebUiAppOptions
{
    public CsWebUiAppOptions(
        WebUIToolkit.Hosting.IFrontendAssetProvider assets,
        WebUIToolkit.Hosting.IRootSessionFactory rootSessionFactory,
        CsWebUiAdapterOptions adapter,
        WebUIToolkit.Hosting.BrowserHostOptions browserHost,
        WebUIToolkit.Hosting.BrowserWindowOptions browserWindow,
        TimeSpan? sessionCloseTimeout = null,
        TimeSpan? windowCloseTimeout = null);
}

public sealed class CsWebUiAppFrontendBuilder
{
    public string? FrontendName { get; }
    public WebUIToolkit.Hosting.WebUiAppBuilder Use(
        string frontendName,
        CsWebUiAppOptions options);
}

// C# 14 extension members on WebUiAppBuilder:
// CsWebUiAppFrontendBuilder CsWebUi { get; }
// WebUiAppBuilder UseCsWebUi(string frontendName, CsWebUiAppOptions options);

// CwhtmlHtmxAppBuilder is declared in the
// WebUIToolkit.MVVM.Html.Htmx.CsWebUi namespace:
public readonly struct CwhtmlHtmxAppBuilder
{
    public WebUIToolkit.Hosting.WebUiAppBuilder Use(CsWebUiAppOptions options);
}

// C# 14 extension members on WebUiAppBuilder:
// CwhtmlHtmxAppBuilder CwhtmlHtmx { get; }
// WebUiAppBuilder UseCwhtmlHtmx(CsWebUiAppOptions options);
```
