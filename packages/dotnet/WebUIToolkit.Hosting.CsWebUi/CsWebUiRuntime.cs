using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi;

internal interface ICsWebUiRuntime
{
    ICsWebUiWindow CreateWindow(Action<WebUiWindow>? configureWindow);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface ICsWebUiWindow : IDisposable
{
    void SetRootFolder(string path);

    void SetSize(uint width, uint height);

    void SetResizable(bool isResizable);

    void SetPublic(bool isPublic);

    IDisposable BindEvents(Action<WebUiEventType> callback);

    void Show(string relativePath, CsWebUiPresentationMode presentationMode, WebUiBrowser browser);

    void Navigate(string relativePath);

    void SetTitle(string title);

    void Close();
}

internal sealed class NativeCsWebUiRuntime : ICsWebUiRuntime
{
    internal static NativeCsWebUiRuntime Instance { get; } = new();

    private NativeCsWebUiRuntime()
    {
    }

    public ICsWebUiWindow CreateWindow(Action<WebUiWindow>? configureWindow)
    {
        var window = new WebUiWindow();
        try
        {
            configureWindow?.Invoke(window);
            return new NativeCsWebUiWindow(window);
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        WebUiApplication.WaitAsync(cancellationToken);
}

internal sealed class NativeCsWebUiWindow : ICsWebUiWindow
{
    private readonly WebUiWindow _window;

    internal NativeCsWebUiWindow(WebUiWindow window)
    {
        _window = window;
    }

    public void SetRootFolder(string path) => _window.SetRootFolder(path);

    public void SetSize(uint width, uint height) => _window.SetSize(width, height);

    public void SetResizable(bool isResizable) => _window.SetResizable(isResizable);

    public void SetPublic(bool isPublic) => _window.SetPublic(isPublic);

    public IDisposable BindEvents(Action<WebUiEventType> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _window.Bind(string.Empty, webUiEvent => callback(webUiEvent.EventType));
    }

    public void Show(
        string relativePath,
        CsWebUiPresentationMode presentationMode,
        WebUiBrowser browser)
    {
        switch (presentationMode)
        {
            case CsWebUiPresentationMode.Auto:
                _window.Show(relativePath);
                break;
            case CsWebUiPresentationMode.Browser:
                _window.ShowInBrowser(relativePath, browser);
                break;
            case CsWebUiPresentationMode.WebView:
                _window.ShowWebView(relativePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(presentationMode));
        }
    }

    public void Navigate(string relativePath)
    {
        string serverUrl = _window.Url ??
            throw new InvalidOperationException("The CsWebUi local server is not running.");
        _window.Navigate(CsWebUiEntryPointPath.BuildNavigationUrl(serverUrl, relativePath));
    }

    public void SetTitle(string title) =>
        _window.RunJavaScript($"document.title = {JsonSerializer.Serialize(title, CsWebUiJsonContext.Default.String)};");

    public void Close() => _window.Close();

    public void Dispose() => _window.Dispose();
}
