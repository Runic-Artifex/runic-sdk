using System.ComponentModel;
using CsWebUi;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.CsWebUi;

// IRunicWindowPresentation of a Runic Window on CS-WebUI, as far as WebUI can: it brings its window to the front
// (WebUiWindow.Focus) and minimizes a WebView window. A browser window reports no focus, minimize or visibility changes
// to WebUI and cannot be hidden, so the Window reports no activity and counts as active and visible.
// sdk#1149: a WebView window on Windows and Linux also reports and takes its placement (WebUiWindow.TryGetPlacement and
// TrySetPlacement) and maximizes and restores. WebUI raises no event when the window moves or resizes, so the placement
// is polled while the window is shown (see PollInterval), and read again when the window closes.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "StopPolling disposes the poll's source when the window closes; the presentation outlives it.")]
internal sealed class CsWebUiWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    private static readonly PlatformResult<PlatformUnit> NotPlaced = new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other,
        new PlatformDiagnostic("CS-WebUI", 0, "CS-WebUI could not read or place the window."));

    // How often a shown WebView window's placement is read. A Win32 subclass could report WM_WINDOWPOSCHANGED instead,
    // but only on the window's own thread, which runs WebUI's message loop, and GTK offers no other thread a signal: one
    // read on Windows is a few user32 calls, on Linux one turn of GTK's main loop.
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly bool _webView;
    private readonly bool _reportsPlacement;
    private readonly object _gate = new();
    private RunicWindowPlacement? _placement;
    private CancellationTokenSource? _poll;

    internal CsWebUiWindowPresentation(WebUiWindow window, bool webView)
        : this(window, webView, webView && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
    {
    }

    // reportsPlacement: whether CS-WebUI reads and sets this window's placement (WebView2, WebKitGTK); tests set it.
    internal CsWebUiWindowPresentation(WebUiWindow window, bool webView, bool reportsPlacement)
    {
        _webView = webView;
        _reportsPlacement = reportsPlacement;
        IsShown = () => window.IsShown;
        Focus = window.Focus;
        Minimize = window.Minimize;
        ReadPlacement = window.TryGetPlacement;
        WritePlacement = window.TrySetPlacement;
    }

    internal delegate bool PlacementReader(out WebUiWindowPlacement placement);

    public event PropertyChangedEventHandler? PropertyChanged;

    event EventHandler<RunicWindowActivityChangedEventArgs>? IRunicWindowPresentation.ActivityChanged { add { } remove { } }

    // Tests replace the native window's state and calls.
    internal Func<bool> IsShown { get; init; }
    internal Action Focus { get; init; }
    internal Action Minimize { get; init; }
    internal PlacementReader ReadPlacement { get; init; }
    internal Func<WebUiWindowPlacement, bool> WritePlacement { get; init; }
    internal Func<IReadOnlyList<ScreenRect>> WorkAreas { get; init; } = CsWebUiWorkAreas.Read;

    // Whether CS-WebUI reads and sets this window's placement.
    internal bool ReportsPlacement => _reportsPlacement;

    public RunicWindowPresentationFeatures Features => !IsShown()
        ? RunicWindowPresentationFeatures.None
        : RunicWindowPresentationFeatures.Present
          | (_webView ? RunicWindowPresentationFeatures.Minimize : RunicWindowPresentationFeatures.None)
          | (_reportsPlacement ? RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement : RunicWindowPresentationFeatures.None);

    public RunicWindowActivity Activity => RunicWindowActivity.Assumed;

    public bool IsActive => true;

    public bool IsMinimized => false;

    public bool IsVisible => true;

    public DesktopColorScheme ColorScheme => DesktopColorScheme.NoPreference;

    // The placement last read; it stays readable after the window closed.
    public RunicWindowPlacement? Placement => Volatile.Read(ref _placement);

    // Reads the placement now and reports a change, for example when the window closes. False when it could not be read.
    internal bool Refresh()
    {
        // A closed window is not read: on Linux the read needs GTK's main loop, which may have stopped.
        if (!_reportsPlacement || !IsShownSafely() || !TryRead(out var native)) return false;
        var placement = CsWebUiPlacement.ToRunic(native);
        lock (_gate)
        {
            if (Equals(_placement, placement)) return true;
            Volatile.Write(ref _placement, placement);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Placement)));
        return true;
    }

    // Polls the placement while the window is shown, from the first read on; Stop or the window's close ends it.
    internal void StartPolling()
    {
        if (!_reportsPlacement) return;
        CancellationTokenSource poll;
        lock (_gate)
        {
            if (_poll is not null) return;
            poll = _poll = new CancellationTokenSource();
        }
        Refresh();
        _ = PollAsync(poll.Token);
    }

    internal void StopPolling()
    {
        CancellationTokenSource? poll;
        lock (_gate) (poll, _poll) = (_poll, null);
        if (poll is null) return;
        poll.Cancel();
        poll.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsShownSafely()) break;
                Refresh();
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool IsShownSafely()
    {
        try { return IsShown(); }
        catch (ObjectDisposedException) { return false; }
    }

    private bool TryRead(out WebUiWindowPlacement placement)
    {
        try { return ReadPlacement(out placement); }
        catch (ObjectDisposedException)
        {
            placement = default;
            return false;
        }
    }

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

    // sdk#1149: the restored bounds stay, the window maximizes or returns to them; WebUI cannot leave a minimized GTK window.
    public ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default) =>
        Place(RunicWindowPresentationFeatures.Maximize, static current => current with { IsMaximized = true }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default) =>
        Place(RunicWindowPresentationFeatures.Maximize, static current => current with { IsMaximized = false }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        RunicWindowPlacement.Validate(placement);
        // CS-WebUI has no full screen, and Wayland gives the application no position: such a placement changes nothing.
        return Place(RunicWindowPresentationFeatures.Placement, current =>
            placement.FullScreen || (placement.X is not null && !current.HasPosition) ? null : CsWebUiPlacement.Fit(placement, current, WorkAreas()),
            cancellationToken);
    }

    // Applies RunicCsWebUiHostOptions.Placement once the window is shown, kept on a work area that exists now.
    internal bool ApplyInitial(RunicWindowPlacement placement)
    {
        if (!_reportsPlacement || !TryRead(out var current)) return false;
        if (!current.HasPosition) placement = placement with { X = null, Y = null };
        var placed = WritePlacement(CsWebUiPlacement.Fit(placement, current, WorkAreas()));
        Refresh();
        return placed;
    }

    private ValueTask<PlatformResult<PlatformUnit>> Place(RunicWindowPresentationFeatures feature,
        Func<WebUiWindowPlacement, WebUiWindowPlacement?> change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsShownSafely()) return ValueTask.FromResult(Closed);
        if (!Features.HasFlag(feature)) return ValueTask.FromResult(Unsupported);
        try
        {
            if (!ReadPlacement(out var current)) return ValueTask.FromResult(NotPlaced);
            if (change(current) is not { } target) return ValueTask.FromResult(Unsupported);
            if (!WritePlacement(target)) return ValueTask.FromResult(NotPlaced);
        }
        catch (ObjectDisposedException) { return ValueTask.FromResult(Closed); }
        Refresh();
        return ValueTask.FromResult(Done);
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
