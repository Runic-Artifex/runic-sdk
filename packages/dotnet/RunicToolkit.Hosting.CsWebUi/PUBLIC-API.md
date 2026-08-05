# RunicToolkit.Hosting.CsWebUi public API

All adapter types use the `RunicToolkit.Hosting.CsWebUi` namespace.

```csharp
public enum CsWebUiPresentationMode
{
    Auto = 0,
    Browser = 1,
    WebView = 2,
}

public sealed record CsWebUiAdapterOptions
{
    public CsWebUiAdapterOptions(
        string webRoot,
        CsWebUiPresentationMode presentationMode = CsWebUiPresentationMode.Auto,
        CsWebUi.WebUiBrowser browser = CsWebUi.WebUiBrowser.AnyBrowser,
        Action<CsWebUi.WebUiWindow>? configureWindow = null);

    public string WebRoot { get; }
    public CsWebUiPresentationMode PresentationMode { get; }
    public CsWebUi.WebUiBrowser Browser { get; }
    public Action<CsWebUi.WebUiWindow>? ConfigureWindow { get; }
}

public sealed class CsWebUiBrowserHostFactory : RunicToolkit.Hosting.IBrowserHostFactory
{
    public CsWebUiBrowserHostFactory(CsWebUiAdapterOptions options);

    public ValueTask<RunicToolkit.Hosting.IBrowserHost> CreateAsync(
        RunicToolkit.Hosting.BrowserHostOptions options,
        CancellationToken cancellationToken);
}

```
