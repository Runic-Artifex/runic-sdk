using System.ComponentModel;
using CsWebUi;
using Runic.Platform;

namespace Runic.Application.Views.CsWebUi;

// IRunicWindowPresentation of a Runic Window on CS-WebUI, as far as WebUI can: it brings its window to the front
// (WebUiWindow.Focus) and minimizes a WebView window. A browser window reports no focus, minimize or visibility changes
// to WebUI and cannot be hidden, so the Window reports no activity and counts as active and visible.
internal sealed class CsWebUiWindowPresentation(WebUiWindow window, bool webView) : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged { add { } remove { } }

    event EventHandler<RunicWindowActivityChangedEventArgs>? IRunicWindowPresentation.ActivityChanged { add { } remove { } }

    // Tests replace the native window's state and calls.
    internal Func<bool> IsShown { get; init; } = () => window.IsShown;
    internal Action Focus { get; init; } = window.Focus;
    internal Action Minimize { get; init; } = window.Minimize;

    public RunicWindowPresentationFeatures Features => !IsShown()
        ? RunicWindowPresentationFeatures.None
        : RunicWindowPresentationFeatures.Present | (webView ? RunicWindowPresentationFeatures.Minimize : RunicWindowPresentationFeatures.None);

    public RunicWindowActivity Activity => RunicWindowActivity.Assumed;

    public bool IsActive => true;

    public bool IsMinimized => false;

    public bool IsVisible => true;

    public DesktopColorScheme ColorScheme => DesktopColorScheme.NoPreference;

    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        Run(RunicWindowPresentationFeatures.Present, Focus, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Run(RunicWindowPresentationFeatures.Minimize, Minimize, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Run(RunicWindowPresentationFeatures.Visibility, static () => { }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Run(RunicWindowPresentationFeatures.FullScreen, static () => { }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Run(RunicWindowPresentationFeatures.DocumentEdited, static () => { }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return Run(RunicWindowPresentationFeatures.ColorScheme, static () => { }, cancellationToken);
    }

    private ValueTask<PlatformResult<PlatformUnit>> Run(RunicWindowPresentationFeatures feature, Action request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsShown()) return ValueTask.FromResult(Closed);
        if (!Features.HasFlag(feature)) return ValueTask.FromResult(Unsupported);
        try
        {
            request();
            return ValueTask.FromResult(Done);
        }
        catch (ObjectDisposedException) { return ValueTask.FromResult(Closed); }
        catch (WebUiException error) { return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, new PlatformDiagnostic("CS-WebUI", 0, error.Message))); }
    }
}
