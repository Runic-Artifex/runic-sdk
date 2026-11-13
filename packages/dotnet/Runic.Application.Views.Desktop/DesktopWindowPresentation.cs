using System.ComponentModel;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// IRunicWindowPresentation of a Runic Window on Runic Desktop: its DesktopWindow, embedded or an installed browser after
// a fallback. RunicDesktopHost gives it to the Window once the window opened (RunicWindowOpening.UsePresentation); the
// Window raises its changes in its model turns.
internal sealed class DesktopWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    internal static readonly PlatformDiagnostic KioskStaysFullScreen = new("Runic.Desktop", 0, "A kiosk window stays full-screen.");

    private readonly DesktopWindow _window;
    private readonly Func<DesktopNativeOwner?> _owner;
    private readonly object _gate = new();
    private RunicWindowActivity _activity;
    // sdk#897, W1b: the window's placement, for a window whose host reports it (WindowPlacement); kept after it closed.
    private readonly bool _reportsPlacement;
    private RunicWindowPlacement? _placement;

    internal DesktopWindowPresentation(DesktopWindow window, Func<DesktopNativeOwner?> owner)
    {
        _window = window;
        _owner = owner;
        _activity = Convert(window.Activity);
        _reportsPlacement = window.Capabilities.HasFlag(DesktopWindowCapabilities.WindowPlacement);
        if (_reportsPlacement)
        {
            _placement = window.State?.Placement.ToRunicPlacement();
            window.StateChanged += OnStateChanged;
        }
        window.ActivityChanged += OnActivityChanged;
        // Also a change another window or the application made to a shared scheme.
        window.ColorSchemeChanged += OnColorSchemeChanged;
    }

    private void OnColorSchemeChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorScheme)));

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    // DesktopPlatformServices.PresentAsync, tests replace it.
    internal Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> Present
    { get; init; } = DesktopPlatformServices.PresentAsync;

    public RunicWindowPresentationFeatures Features =>
        FeaturesOf(_window.Capabilities, _window.IsOpen, OperatingSystem.IsMacOS()) | SidePanelFeature();

    // DesktopWindow has no capability flag for the panel: CheckSidePanel answers Unsupported where its host has none.
    private RunicWindowPresentationFeatures SidePanelFeature()
    {
        try
        {
            return _window.IsOpen && _window.CheckSidePanel(DesktopWindowEdge.End, DesktopSidePanelOptions.MinimumWidth) != DesktopSidePanelRefusal.Unsupported
                ? RunicWindowPresentationFeatures.SidePanel
                : RunicWindowPresentationFeatures.None;
        }
        catch (ObjectDisposedException)
        {
            return RunicWindowPresentationFeatures.None;
        }
    }

    internal static RunicWindowPresentationFeatures FeaturesOf(DesktopWindowCapabilities capabilities, bool open, bool macOS)
    {
        if (!open) return RunicWindowPresentationFeatures.None;
        var features = RunicWindowPresentationFeatures.None;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Focus)) features |= RunicWindowPresentationFeatures.Present;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Minimize)) features |= RunicWindowPresentationFeatures.Minimize;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Visibility)) features |= RunicWindowPresentationFeatures.Visibility;
        if (capabilities.HasFlag(DesktopWindowCapabilities.FullScreen)) features |= RunicWindowPresentationFeatures.FullScreen;
        if (capabilities.HasFlag(DesktopWindowCapabilities.ColorScheme)) features |= RunicWindowPresentationFeatures.ColorScheme;
        if (capabilities.HasFlag(DesktopWindowCapabilities.ColorScheme | DesktopWindowCapabilities.PerWindowColorScheme))
            features |= RunicWindowPresentationFeatures.PerWindowColorScheme;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Activity)) features |= RunicWindowPresentationFeatures.Activity;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Ownership)) features |= RunicWindowPresentationFeatures.OwnedWindows;
        if (capabilities.HasFlag(DesktopWindowCapabilities.WindowPlacement))
            features |= RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement;
        // NSWindow.documentEdited: only the macOS window shows it; elsewhere DesktopWindow accepts it and does nothing.
        if (macOS && capabilities.HasFlag(DesktopWindowCapabilities.NativeHandle)) features |= RunicWindowPresentationFeatures.DocumentEdited;
        return features;
    }

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme => _window.ColorScheme;

    // A window that reports no activity counts as active and visible while it is open.
    internal static RunicWindowActivity Convert(DesktopWindowActivity? activity) => activity is null
        ? RunicWindowActivity.Assumed
        : new RunicWindowActivity(activity.IsActive, activity.IsMinimized, activity.IsVisible);

    private void OnActivityChanged(object? sender, DesktopWindowActivityChangedEventArgs args)
    {
        var activity = Convert(args.Activity);
        RunicWindowActivity previous;
        lock (_gate)
        {
            previous = _activity;
            if (Equals(previous, activity)) return;
            Volatile.Write(ref _activity, activity);
        }
        // DesktopWindow raises its events one at a time, so the changes keep their order.
        ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
        if (!_window.IsOpen) Detach();
    }

    public RunicWindowPlacement? Placement => Volatile.Read(ref _placement);

    // DesktopWindow.State.Placement, which excludes an open side panel. StateChanged is raised one at a time and combines
    // the changes of a drag; a change of the current frame alone keeps the placement.
    private void OnStateChanged(object? sender, DesktopWindowStateChangedEventArgs args)
    {
        var placement = args.State.Placement.ToRunicPlacement();
        lock (_gate)
        {
            if (Equals(_placement, placement)) return;
            Volatile.Write(ref _placement, placement);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Placement)));
    }

    // Stops following the window, which then no longer holds this presentation (DesktopWindowServices).
    internal void Detach()
    {
        _window.StateChanged -= OnStateChanged;
        _window.ActivityChanged -= OnActivityChanged;
        _window.ColorSchemeChanged -= OnColorSchemeChanged;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_window.IsOpen) return Closed;
        if (!Features.HasFlag(RunicWindowPresentationFeatures.Present)) return Unsupported;
        // The platform's presentation shows a hidden window, restores a minimized one, asks for the foreground with the
        // desktop's rules and passes the activation token. Without a configured provider, or for a custom window host
        // without native dispatch, the window's own focus shows and activates it.
        if (_owner() is { IsAvailable: true } owner)
        {
            var presented = await Present(owner, activation, cancellationToken).ConfigureAwait(false);
            if (presented is not PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured })
                return presented;
        }
        return await RunAsync(() => _window.FocusAsync(cancellationToken)).ConfigureAwait(false);
    }

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Minimize, () => _window.MinimizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Visibility, () => _window.SetVisibleAsync(visible, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.FullScreen, () => _window.SetFullScreenAsync(fullScreen, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.DocumentEdited, () => _window.SetDocumentEditedAsync(edited, cancellationToken), cancellationToken);

    public async ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        // DesktopWindow.ColorSchemeChanged reports the change.
        return await Request(RunicWindowPresentationFeatures.ColorScheme,
            () => _window.SetColorSchemeAsync(colorScheme, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // sdk#897, W1b (window-api-0.19/DESIGN.md §3.2): DesktopWindow's own requests, in its placement units.
    public ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Maximize, () => _window.MaximizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Maximize, () => _window.RestoreAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        RunicWindowPlacement.Validate(placement);
        var desktop = placement.ToDesktopPlacement();
        return Request(RunicWindowPresentationFeatures.Placement, () => _window.SetPlacementAsync(desktop, cancellationToken), cancellationToken);
    }

    // sdk#864, P5 (side-panel/DESIGN.md §3.2): the Window's side panel is its DesktopWindow's.
    private PanelAdapter? _sidePanel;

    // Also a panel opened on the DesktopWindow itself, adopted when it is first read.
    public IRunicSidePanel? SidePanel => _window.SidePanel is { } native ? Adopt(native) : null;

    private PanelAdapter Adopt(DesktopSidePanel native)
    {
        PanelAdapter panel;
        lock (_gate)
        {
            if (_sidePanel is { } current && ReferenceEquals(current.Native, native)) return current;
            panel = new PanelAdapter(native);
            _sidePanel = panel;
        }
        panel.Start(this);
        return panel;
    }

    public async ValueTask<RunicSidePanelResult> OpenSidePanelAsync(RunicSidePanelOptions options, CancellationToken cancellationToken = default)
    {
        RunicSidePanelOptions.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_window.IsOpen) return new RunicSidePanelResult.Refused(RunicSidePanelRefusal.Closed);
        DesktopSidePanelResult result;
        try
        {
            result = await _window.OpenSidePanelAsync(new DesktopSidePanelOptions
            {
                Edge = Convert(options.Edge), Width = options.Width, Path = options.Path, LoadTimeout = options.LoadTimeout,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new RunicSidePanelResult.Refused(RunicSidePanelRefusal.Closed);
        }
        if (result is DesktopSidePanelResult.Refused refused) return new RunicSidePanelResult.Refused(Convert(refused.Reason));
        var opened = (DesktopSidePanelResult.Opened)result;
        var panel = Adopt(opened.Panel);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidePanel)));
        return new RunicSidePanelResult.Opened(panel);
    }

    private void OnSidePanelClosed(PanelAdapter panel)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_sidePanel, panel)) return;
            _sidePanel = null;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidePanel)));
    }

    internal static DesktopWindowEdge Convert(RunicSidePanelEdge edge) => edge switch
    {
        RunicSidePanelEdge.Left => DesktopWindowEdge.Left,
        RunicSidePanelEdge.Right => DesktopWindowEdge.Right,
        RunicSidePanelEdge.Start => DesktopWindowEdge.Start,
        _ => DesktopWindowEdge.End,
    };

    // An unknown value of a later Runic.Desktop counts as Unsupported, as DesktopSidePanelRefusal says.
    internal static RunicSidePanelRefusal Convert(DesktopSidePanelRefusal refusal) => refusal switch
    {
        DesktopSidePanelRefusal.Maximized => RunicSidePanelRefusal.Maximized,
        DesktopSidePanelRefusal.FullScreen => RunicSidePanelRefusal.FullScreen,
        DesktopSidePanelRefusal.Snapped => RunicSidePanelRefusal.Snapped,
        DesktopSidePanelRefusal.Minimized => RunicSidePanelRefusal.Minimized,
        DesktopSidePanelRefusal.NoRoom => RunicSidePanelRefusal.NoRoom,
        DesktopSidePanelRefusal.PositionUnavailable => RunicSidePanelRefusal.PositionUnavailable,
        DesktopSidePanelRefusal.AlreadyOpen => RunicSidePanelRefusal.AlreadyOpen,
        DesktopSidePanelRefusal.LoadFailed => RunicSidePanelRefusal.LoadFailed,
        _ => RunicSidePanelRefusal.Unsupported,
    };

    internal static RunicSidePanelCloseReason Convert(DesktopSidePanelCloseReason reason) => reason switch
    {
        DesktopSidePanelCloseReason.WindowClosed => RunicSidePanelCloseReason.WindowClosed,
        DesktopSidePanelCloseReason.Crashed => RunicSidePanelCloseReason.Crashed,
        _ => RunicSidePanelCloseReason.Closed,
    };

    // IRunicSidePanel of a DesktopSidePanel. Its Closed is raised on the thread pool, as DesktopSidePanel's; the Window's
    // RunicWindowPresentation raises it again in a model turn. Edge and width are kept, so they stay readable once it closed.
    private sealed class PanelAdapter(DesktopSidePanel native) : IRunicSidePanel
    {
        private readonly object _gate = new();
        private EventHandler<RunicSidePanelClosedEventArgs>? _closedHandlers;
        private bool _raised;
        private volatile RunicSidePanelEdge _edge = EdgeOf(native);
        private int _width = native.Width;
        private DesktopWindowPresentation? _presentation;

        internal DesktopSidePanel Native => native;

        private static RunicSidePanelEdge EdgeOf(DesktopSidePanel panel) =>
            panel.Edge == DesktopWindowEdge.Left ? RunicSidePanelEdge.Left : RunicSidePanelEdge.Right;

        public RunicSidePanelEdge Edge => _edge;

        public int Width => Volatile.Read(ref _width);

        public bool IsOpen => native.IsOpen;

        public event EventHandler<RunicSidePanelClosedEventArgs>? Closed
        {
            add { lock (_gate) if (!_raised) _closedHandlers += value; }
            remove { lock (_gate) _closedHandlers -= value; }
        }

        internal void Start(DesktopWindowPresentation presentation)
        {
            _presentation = presentation;
            native.Closed += OnClosed;
            // Closed before the handler was added (the window closed meanwhile): DesktopSidePanel does not call it then.
            if (!native.IsOpen) OnClosed(native, new DesktopSidePanelClosedEventArgs(DesktopSidePanelCloseReason.WindowClosed));
        }

        private void OnClosed(object? sender, DesktopSidePanelClosedEventArgs args)
        {
            EventHandler<RunicSidePanelClosedEventArgs>? handlers;
            lock (_gate)
            {
                if (_raised) return;
                _raised = true;
                handlers = _closedHandlers;
                _closedHandlers = null;
            }
            native.Closed -= OnClosed;
            _presentation?.OnSidePanelClosed(this);
            handlers?.Invoke(this, new RunicSidePanelClosedEventArgs(Convert(args.Reason)));
        }

        public async ValueTask<RunicSidePanelRefusal?> MoveToEdgeAsync(RunicSidePanelEdge edge, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateEdge(edge);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await native.MoveToEdgeAsync(Convert(edge), cancellationToken).ConfigureAwait(false) is { } refusal) return Convert(refusal);
            }
            catch (ObjectDisposedException) { return RunicSidePanelRefusal.Closed; }
            catch (InvalidOperationException) when (!native.IsOpen) { return RunicSidePanelRefusal.Closed; }
            if (native.IsOpen) _edge = EdgeOf(native);
            return null;
        }

        public async ValueTask<RunicSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateWidth(width);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await native.SetWidthAsync(width, cancellationToken).ConfigureAwait(false) is { } refusal) return Convert(refusal);
            }
            catch (ObjectDisposedException) { return RunicSidePanelRefusal.Closed; }
            catch (InvalidOperationException) when (!native.IsOpen) { return RunicSidePanelRefusal.Closed; }
            Volatile.Write(ref _width, width);
            return null;
        }

        public async ValueTask FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await native.FocusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) when (!native.IsOpen) { }
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => native.CloseAsync(cancellationToken);

        public ValueTask DisposeAsync() => native.DisposeAsync();
    }

    private ValueTask<PlatformResult<PlatformUnit>> Request(RunicWindowPresentationFeatures feature, Func<ValueTask> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_window.IsOpen) return ValueTask.FromResult(Closed);
        return Features.HasFlag(feature) ? RunAsync(request) : ValueTask.FromResult(Unsupported);
    }

    // DesktopWindow throws where the presentation reports results: a window closed meanwhile, a capability it lost.
    private async ValueTask<PlatformResult<PlatformUnit>> RunAsync(Func<ValueTask> request)
    {
        try
        {
            await request().ConfigureAwait(false);
            return Done;
        }
        catch (ObjectDisposedException) { return Closed; }
        catch (DesktopException error) when (error.Code == "window-capability-unavailable") { return Unsupported; }
        // A placement with a position on Wayland (D064.11): nothing changed.
        catch (DesktopException error) when (error.Code == "window-position-unavailable") { return Unsupported; }
        catch (InvalidOperationException) when (!_window.IsOpen) { return Closed; }
        catch (InvalidOperationException) when (_window.State?.IsFullScreen == true)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict, KioskStaysFullScreen);
        }
        catch (NotSupportedException) { return Unsupported; }
    }
}
