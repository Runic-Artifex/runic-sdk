using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Base class for an application Window presented by CS-WebUI. Declare one
/// partial subclass per Window to select its root View contract:
/// <code>
/// public sealed partial class MainWindow(CsWebUiBridgeWindow&lt;MainViewModel&gt; host)
///     : CsWebUiWindow&lt;MainViewModel&gt;(host);
/// </code>
/// Open it with <c>provider.OpenWindow&lt;MainWindow, MainViewModel&gt;(host =&gt; new MainWindow(host))</c>.
/// The members forward to <see cref="Host"/>, which owns the native window,
/// its DI scope, and its Bridge attachments.
/// </summary>
public abstract class CsWebUiWindow<TViewModel> : RunicWindow<TViewModel>, IDisposable, IAsyncDisposable
    where TViewModel : class
{
    protected CsWebUiWindow(CsWebUiBridgeWindow<TViewModel> host)
        : base((host ?? throw new ArgumentNullException(nameof(host))).ViewModel) => Host = host;

    /// <summary>The CS-WebUI host adapter for this Window.</summary>
    public CsWebUiBridgeWindow<TViewModel> Host { get; }

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.SetRootFolder"/>
    public void SetRootFolder(string path) => Host.SetRootFolder(path);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.SetSize"/>
    public void SetSize(uint width, uint height) => Host.SetSize(width, height);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.Show"/>
    public void Show(string content) => Host.Show(content);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.ShowWebView"/>
    public void ShowWebView(string content) => Host.ShowWebView(content);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.StartServer"/>
    public string StartServer(string content) => Host.StartServer(content);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.CloseAsync"/>
    public ValueTask<CsWebUiBridgeCloseResult> CloseAsync(TimeSpan timeout) => Host.CloseAsync(timeout);

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.Dispose"/>
    public void Dispose()
    {
        Host.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc cref="CsWebUiBridgeWindow{TViewModel}.DisposeAsync"/>
    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
