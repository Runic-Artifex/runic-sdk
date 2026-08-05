# RunicToolkit.Hosting.CsWebUi.App public API

All types use the `RunicToolkit.Hosting.CsWebUi` namespace.

```csharp
public sealed record CsWebUiAppOptions
{
    public CsWebUiAppOptions(
        RunicToolkit.Hosting.IFrontendAssetProvider assets,
        RunicToolkit.Hosting.IRootSessionFactory rootSessionFactory,
        CsWebUiAdapterOptions adapter,
        RunicToolkit.Hosting.BrowserHostOptions browserHost,
        RunicToolkit.Hosting.BrowserWindowOptions browserWindow,
        TimeSpan? sessionCloseTimeout = null,
        TimeSpan? windowCloseTimeout = null);
}

public sealed class CsWebUiAppFrontendBuilder
{
    public string? FrontendName { get; }
    public RunicToolkit.Hosting.WebUiAppBuilder Use(
        string frontendName,
        CsWebUiAppOptions options);
}

// C# 14 extension members on WebUiAppBuilder:
// CsWebUiAppFrontendBuilder CsWebUi { get; }
// WebUiAppBuilder UseCsWebUi(string frontendName, CsWebUiAppOptions options);

```
