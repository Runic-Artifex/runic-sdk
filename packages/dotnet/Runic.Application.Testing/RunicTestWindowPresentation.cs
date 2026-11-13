using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// A recording <see cref="IRunicWindowPresentation"/> for ViewModel tests: it records every request, behaves like a
/// window that obeys them, and lets the test set the activity, for example to check that a ViewModel pauses polling
/// while its Window is inactive.
/// </summary>
/// <remarks>
/// <para>
/// Register it for the window's scope with <see cref="RunicTestWindowPresentationServiceCollectionExtensions.AddRunicTestWindowPresentation"/>.
/// The scope's <see cref="IRunicWindowPresentation"/> is then the same per-Window presentation an application gets,
/// with this fake as its host: changes reach the ViewModel in a turn of the window's model context after the request
/// that caused them completed, a handler that throws is logged (event 1130) without stopping the others, and once the
/// scope is disposed requests report <see cref="PlatformUnavailableReason.OwnerClosed"/>.
/// <see cref="RunicWindowTestHost{TViewModel}.Presentation"/> returns the fake,
/// <see cref="RunicWindowTestHost{TViewModel}.SetWindowActivityAsync"/> changes its activity and waits until the
/// ViewModel received it, and <see cref="RunicWindowTestHost{TViewModel}.FlushWindowPresentationAsync"/> waits for the
/// changes a command's requests caused:
/// <code>
/// await using var services = new ServiceCollection().AddRunicViews().AddScoped&lt;InboxViewModel&gt;()
///     .AddRunicTestWindowPresentation().BuildServiceProvider();
/// await using var scope = services.CreateAsyncScope();
/// using var host = RunicWindowTestHost.Create&lt;InboxViewModel&gt;(scope.ServiceProvider);
/// (await host.Root.ExecuteAsync(vm =&gt; vm.HideToTrayCommand)).ThrowIfFailed();
/// Assert.False(host.Presentation!.IsVisible);
/// await host.SetWindowActivityAsync(RunicWindowActivity.Assumed);
/// </code>
/// </para>
/// <para>
/// It behaves as a desktop window does: a successful <see cref="PresentAsync"/> shows, restores and activates it,
/// <see cref="MinimizeAsync"/> minimizes it, <see cref="SetVisibleAsync"/> with <see langword="false"/> hides it, and
/// with <see langword="true"/> shows it again without restoring it, so a minimized window stays minimized, as Win32's
/// <c>SW_SHOW</c> leaves it. A request whose feature is not in <see cref="Features"/> reports
/// <see cref="PlatformUnavailableReason.BackendUnavailable"/> without changing anything; <see cref="Respond"/> chooses
/// another result.
/// </para>
/// <para>
/// Used directly, as a ViewModel built by hand takes it or as <c>AddRunicTestWindowPresentation(immediate: true)</c>
/// registers it, the fake raises its changes on the calling thread at once, inside the request, and keeps answering
/// after the scope ends. A change that a handler causes is raised after that handler returns, not inside it.
/// </para>
/// <para>
/// It also decides a side panel (<see cref="OpenSidePanelAsync"/>, experimental under <c>RUNICDESK104</c>) as a Runic
/// Desktop window on Windows does: an 860-wide window at <see cref="WindowX"/> 530 on a 1920-wide work area that grows
/// on the panel's edge and shrinks back when it closes. <see cref="IsMaximized"/>, <see cref="IsSnapped"/>, the
/// activity, <see cref="IsFullScreen"/> and the room produce the refusals; <see cref="SetSidePanelRefusal"/> chooses
/// one, and <see cref="CloseSidePanel"/> closes the panel as its page or a crash would.
/// </para>
/// </remarks>
public sealed class RunicTestWindowPresentation : IRunicWindowPresentation
{
    /// <summary>Every feature a desktop window can have.</summary>
    public const RunicWindowPresentationFeatures AllFeatures = RunicWindowPresentationFeatures.Present |
        RunicWindowPresentationFeatures.Minimize | RunicWindowPresentationFeatures.Visibility |
        RunicWindowPresentationFeatures.FullScreen | RunicWindowPresentationFeatures.DocumentEdited |
        RunicWindowPresentationFeatures.ColorScheme | RunicWindowPresentationFeatures.PerWindowColorScheme |
        RunicWindowPresentationFeatures.Activity | RunicWindowPresentationFeatures.OwnedWindows |
        RunicWindowPresentationFeatures.SidePanel;

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private readonly object _gate = new();
    private readonly List<RunicWindowPresentationRequest> _requests = [];
    private readonly Queue<Action> _raises = new();
    private bool _raising;
    private RunicWindowActivity _activity;
    // The window's own state, from which the activity follows: hidden and minimized are independent, as on the desktop.
    private bool _hidden;
    private bool _minimized;
    private DesktopColorScheme _colorScheme;

    /// <summary>Creates a window that is active and visible, with every feature.</summary>
    public RunicTestWindowPresentation() : this(AllFeatures, RunicWindowActivity.Assumed)
    {
    }

    /// <summary>Creates a window with <paramref name="features"/> and <paramref name="activity"/>.</summary>
    /// <param name="features">What the window can do, such as <see cref="RunicWindowPresentationFeatures.Present"/> alone for a CS-WebUI window.</param>
    /// <param name="activity">The activity it starts with.</param>
    public RunicTestWindowPresentation(RunicWindowPresentationFeatures features, RunicWindowActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Features = features;
        _activity = activity;
        _minimized = activity.IsMinimized;
        _hidden = !activity.IsVisible && !activity.IsMinimized;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    /// <summary>Gets or sets what the window can do; the default is <see cref="AllFeatures"/>.</summary>
    public RunicWindowPresentationFeatures Features { get; set; }

    /// <summary>
    /// Gets or sets the result of each request whose feature is in <see cref="Features"/>, or <see langword="null"/> for
    /// success. A request that does not succeed changes nothing.
    /// </summary>
    /// <remarks>For example, a desktop that keeps another application in front:
    /// <c>request =&gt; request.Kind == RunicWindowPresentationRequestKind.Present ? new PlatformResult&lt;PlatformUnit&gt;.Failed(PlatformFailureCode.PermissionDenied) : null</c>.</remarks>
    public Func<RunicWindowPresentationRequest, PlatformResult<PlatformUnit>?>? Respond { get; set; }

    /// <summary>Gets every request in the order it arrived, including those that did not succeed.</summary>
    public IReadOnlyList<RunicWindowPresentationRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>Gets how many times the window was asked to present itself.</summary>
    public int PresentCount
    {
        get { lock (_gate) return _requests.Count(static request => request.Kind == RunicWindowPresentationRequestKind.Present); }
    }

    /// <summary>Gets whether the window is full-screen after the requests so far.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Gets whether the window's document is marked edited after the requests so far.</summary>
    public bool IsDocumentEdited { get; private set; }

    /// <inheritdoc />
    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    /// <inheritdoc />
    public bool IsActive => Activity.IsActive;

    /// <inheritdoc />
    public bool IsMinimized => Activity.IsMinimized;

    /// <inheritdoc />
    public bool IsVisible => Activity.IsVisible;

    /// <inheritdoc />
    public DesktopColorScheme ColorScheme => _colorScheme;

    /// <summary>
    /// Changes the activity as the desktop would, and raises <see cref="ActivityChanged"/> and
    /// <see cref="PropertyChanged"/> on the calling thread when it changed.
    /// </summary>
    /// <param name="activity">The new activity.</param>
    public void SetActivity(RunicWindowActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (_gate)
        {
            _minimized = activity.IsMinimized;
            _hidden = !activity.IsVisible && !activity.IsMinimized;
        }
        Change(activity);
    }

    private void Change(RunicWindowActivity activity)
    {
        RunicWindowActivity previous;
        lock (_gate)
        {
            previous = _activity;
            if (Equals(previous, activity)) return;
            Volatile.Write(ref _activity, activity);
        }
        RaiseInOrder(() =>
        {
            if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
            if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
            if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
            Raise(nameof(Activity));
            ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
        });
    }

    /// <summary>Makes the window active and visible, or inactive and still visible, as switching to another window does.</summary>
    /// <param name="active">Whether the window has the keyboard focus.</param>
    public void SetActive(bool active) => SetActivity(active
        ? new RunicWindowActivity(IsActive: true, IsMinimized: false, IsVisible: true)
        : Activity with { IsActive = false });

    /// <summary>Clears <see cref="Requests"/>.</summary>
    public void ClearRequests()
    {
        lock (_gate) _requests.Clear();
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.Present) { Activation = activation },
            RunicWindowPresentationFeatures.Present, () => Apply(hidden: false, minimized: false, active: true), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.Minimize), RunicWindowPresentationFeatures.Minimize,
            () => Apply(_hidden, minimized: true, active: false), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetVisible) { Value = visible },
            RunicWindowPresentationFeatures.Visibility,
            // Showing neither restores nor activates the window.
            () =>
            {
                Apply(hidden: !visible, _minimized, active: visible && Activity.IsActive);
                VisibilitySet?.Invoke(visible);
            }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetFullScreen) { Value = fullScreen },
            RunicWindowPresentationFeatures.FullScreen, () => IsFullScreen = fullScreen, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetDocumentEdited) { Value = edited },
            RunicWindowPresentationFeatures.DocumentEdited, () => IsDocumentEdited = edited, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetColorScheme) { ColorScheme = colorScheme },
            RunicWindowPresentationFeatures.ColorScheme, () =>
            {
                if (_colorScheme == colorScheme) return;
                _colorScheme = colorScheme;
                RaiseInOrder(() => Raise(nameof(ColorScheme)));
            }, cancellationToken);
    }

    // Raised after SetVisibleAsync hid or showed the window: the test host hides and shows its owned Windows with it.
    internal event Action<bool>? VisibilitySet;

    // The test host hides or shows an owned Window with its owner, as Runic Desktop does, without recording a request.
    internal void SetVisibleWithOwner(bool visible) =>
        Apply(hidden: !visible, _minimized, active: visible && Activity.IsActive);

    private void Apply(bool hidden, bool minimized, bool active)
    {
        lock (_gate) (_hidden, _minimized) = (hidden, minimized);
        Change(new RunicWindowActivity(IsActive: active && !hidden && !minimized, IsMinimized: minimized, IsVisible: !hidden && !minimized));
    }

    private ValueTask<PlatformResult<PlatformUnit>> Request(RunicWindowPresentationRequest request, RunicWindowPresentationFeatures feature,
        Action apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _requests.Add(request);
        var result = (Features & feature) == 0 ? Unsupported : Respond?.Invoke(request) ?? Done;
        if (result is PlatformResult<PlatformUnit>.Success) apply();
        return ValueTask.FromResult(result);
    }

    // A change that a handler causes is raised once the handlers of the current one returned.
    private void RaiseInOrder(Action raise)
    {
        lock (_raises)
        {
            _raises.Enqueue(raise);
            if (_raising) return;
            _raising = true;
        }
        while (true)
        {
            Action next;
            lock (_raises)
            {
                if (!_raises.TryDequeue(out next!))
                {
                    _raising = false;
                    return;
                }
            }
            next();
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    // sdk#864, P5: the side panel, decided as a Runic Desktop window on Windows decides it (side-panel/DESIGN.md §3.2).
    private TestSidePanel? _sidePanel;
    private RunicSidePanelRefusal? _sidePanelRefusal;

    /// <summary>Gets or sets the left edge of the window's frame on the work area, panel included; 530 by default, centred.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>), with the rest of the side panel.</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public int WindowX { get; set; } = 530;

    /// <summary>Gets or sets the width of the window's frame, panel included; 860 by default. An open panel adds its width, closing it subtracts it.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public int WindowWidth { get; set; } = 860;

    /// <summary>Gets or sets the width of the monitor's work area; 1920 by default.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public int WorkAreaWidth { get; set; } = 1920;

    /// <summary>Gets or sets whether the window is maximized, which refuses a side panel with <see cref="RunicSidePanelRefusal.Maximized"/>.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public bool IsMaximized { get; set; }

    /// <summary>Gets or sets whether the desktop arranged the window, which refuses a side panel with <see cref="RunicSidePanelRefusal.Snapped"/>.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public bool IsSnapped { get; set; }

    /// <summary>Gets or sets whether the window lays out right to left, so <see cref="RunicSidePanelEdge.End"/> is its left edge.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public bool IsRightToLeft { get; set; }

    /// <summary>
    /// Makes every side panel request refuse with <paramref name="reason"/>, after <see cref="RunicSidePanelRefusal.Unsupported"/>
    /// and <see cref="RunicSidePanelRefusal.AlreadyOpen"/>, until it is set to <see langword="null"/>: for example
    /// <see cref="RunicSidePanelRefusal.PositionUnavailable"/> as on Wayland, or <see cref="RunicSidePanelRefusal.LoadFailed"/>.
    /// </summary>
    /// <param name="reason">The refusal, or <see langword="null"/> to decide from the window's state and room again.</param>
    /// <remarks>
    /// Without it the window refuses as a desktop window does: <see cref="RunicSidePanelRefusal.Unsupported"/> without
    /// <see cref="RunicWindowPresentationFeatures.SidePanel"/> in <see cref="Features"/>, then
    /// <see cref="RunicSidePanelRefusal.AlreadyOpen"/>, <see cref="RunicSidePanelRefusal.Minimized"/> (also hidden),
    /// <see cref="RunicSidePanelRefusal.FullScreen"/>, <see cref="RunicSidePanelRefusal.Maximized"/>,
    /// <see cref="RunicSidePanelRefusal.Snapped"/> and <see cref="RunicSidePanelRefusal.NoRoom"/>: the window, grown by
    /// the panel on its edge, would leave the work area, or the panel is wider than half of it. Experimental (<c>RUNICDESK104</c>).
    /// </remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public void SetSidePanelRefusal(RunicSidePanelRefusal? reason)
    {
        lock (_gate) _sidePanelRefusal = reason;
    }

    /// <inheritdoc />
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public IRunicSidePanel? SidePanel => Volatile.Read(ref _sidePanel);

    /// <inheritdoc />
    /// <remarks>The request is recorded; an open panel grows <see cref="WindowWidth"/>, and on the left edge moves <see cref="WindowX"/>.</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public ValueTask<RunicSidePanelResult> OpenSidePanelAsync(RunicSidePanelOptions options, CancellationToken cancellationToken = default)
    {
        RunicSidePanelOptions.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        TestSidePanel panel;
        lock (_gate)
        {
            _requests.Add(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.OpenSidePanel)
            {
                SidePanel = options, Edge = options.Edge, Width = options.Width,
            });
            var left = IsLeft(options.Edge);
            if ((Features & RunicWindowPresentationFeatures.SidePanel) == 0) return Refused(RunicSidePanelRefusal.Unsupported);
            if (_sidePanel is not null) return Refused(RunicSidePanelRefusal.AlreadyOpen);
            if (CheckSidePanel(left ? WindowX - options.Width : WindowX, WindowWidth + options.Width, options.Width) is { } refusal)
                return Refused(refusal);
            if (left) WindowX -= options.Width;
            WindowWidth += options.Width;
            panel = new TestSidePanel(this, left, options.Width);
            Volatile.Write(ref _sidePanel, panel);
        }
        RaiseInOrder(() => Raise(nameof(SidePanel)));
        return ValueTask.FromResult<RunicSidePanelResult>(new RunicSidePanelResult.Opened(panel));

        static ValueTask<RunicSidePanelResult> Refused(RunicSidePanelRefusal reason) =>
            ValueTask.FromResult<RunicSidePanelResult>(new RunicSidePanelResult.Refused(reason));
    }

    /// <summary>
    /// Closes the open side panel as its page or the desktop would: <see cref="RunicSidePanelCloseReason.Closed"/> for
    /// <c>window.close()</c> in the panel's page, <see cref="RunicSidePanelCloseReason.Crashed"/> for a crashed page, or
    /// <see cref="RunicSidePanelCloseReason.WindowClosed"/>. Does nothing without one. Not recorded as a request.
    /// </summary>
    /// <param name="reason">Why the panel closed.</param>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public void CloseSidePanel(RunicSidePanelCloseReason reason) => Volatile.Read(ref _sidePanel)?.Close(reason);

    private bool IsLeft(RunicSidePanelEdge edge) => edge switch
    {
        RunicSidePanelEdge.Left => true,
        RunicSidePanelEdge.Right => false,
        RunicSidePanelEdge.Start => !IsRightToLeft,
        _ => IsRightToLeft,
    };

    // The refusal of a window whose frame would be x and width with a panel of panelWidth. Called under _gate.
    private RunicSidePanelRefusal? CheckSidePanel(int x, int width, int panelWidth)
    {
        if (_sidePanelRefusal is { } refusal) return refusal;
        if (_minimized || _hidden) return RunicSidePanelRefusal.Minimized;
        if (IsFullScreen) return RunicSidePanelRefusal.FullScreen;
        if (IsMaximized) return RunicSidePanelRefusal.Maximized;
        if (IsSnapped) return RunicSidePanelRefusal.Snapped;
        if (x < 0 || x + width > WorkAreaWidth || panelWidth * 2 > WorkAreaWidth) return RunicSidePanelRefusal.NoRoom;
        return null;
    }

    private void Record(RunicWindowPresentationRequest request)
    {
        lock (_gate) _requests.Add(request);
    }

    // The fake's panel: it changes the window's frame as Runic Desktop does, and raises Closed once.
    private sealed class TestSidePanel(RunicTestWindowPresentation window, bool left, int width) : IRunicSidePanel
    {
        private readonly object _gate = new();
        private EventHandler<RunicSidePanelClosedEventArgs>? _closedHandlers;
        private bool _left = left;
        private int _width = width;
        private bool _open = true;

        public RunicSidePanelEdge Edge
        {
            get { lock (window._gate) return _left ? RunicSidePanelEdge.Left : RunicSidePanelEdge.Right; }
        }

        public int Width
        {
            get { lock (window._gate) return _width; }
        }

        public bool IsOpen
        {
            get { lock (window._gate) return _open; }
        }

        public event EventHandler<RunicSidePanelClosedEventArgs>? Closed
        {
            add { lock (_gate) if (IsOpen) _closedHandlers += value; }
            remove { lock (_gate) _closedHandlers -= value; }
        }

        public ValueTask<RunicSidePanelRefusal?> MoveToEdgeAsync(RunicSidePanelEdge edge, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateEdge(edge);
            cancellationToken.ThrowIfCancellationRequested();
            window.Record(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.MoveSidePanel) { Edge = edge });
            lock (window._gate)
            {
                if (!_open) return Result(RunicSidePanelRefusal.Closed);
                var toLeft = window.IsLeft(edge);
                if (toLeft == _left) return Result(null);
                var x = toLeft ? window.WindowX - _width : window.WindowX + _width;
                if (window.CheckSidePanel(x, window.WindowWidth, _width) is { } refusal) return Result(refusal);
                window.WindowX = x;
                _left = toLeft;
                return Result(null);
            }
        }

        public ValueTask<RunicSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken = default)
        {
            RunicSidePanelOptions.ValidateWidth(width);
            cancellationToken.ThrowIfCancellationRequested();
            window.Record(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetSidePanelWidth) { Width = width });
            lock (window._gate)
            {
                if (!_open) return Result(RunicSidePanelRefusal.Closed);
                var change = width - _width;
                if (change == 0) return Result(null);
                var x = _left ? window.WindowX - change : window.WindowX;
                if (window.CheckSidePanel(x, window.WindowWidth + change, width) is { } refusal) return Result(refusal);
                window.WindowX = x;
                window.WindowWidth += change;
                _width = width;
                return Result(null);
            }
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window.Record(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.FocusSidePanel));
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return ValueTask.CompletedTask;
            window.Record(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.CloseSidePanel));
            Close(RunicSidePanelCloseReason.Closed);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);

        // The window shrinks back on the panel's edge, then SidePanel and Closed are raised.
        internal void Close(RunicSidePanelCloseReason reason)
        {
            lock (window._gate)
            {
                if (!_open) return;
                _open = false;
                if (_left) window.WindowX += _width;
                window.WindowWidth -= _width;
                if (ReferenceEquals(window._sidePanel, this)) Volatile.Write(ref window._sidePanel, null);
            }
            EventHandler<RunicSidePanelClosedEventArgs>? handlers;
            lock (_gate)
            {
                handlers = _closedHandlers;
                _closedHandlers = null;
            }
            window.RaiseInOrder(() =>
            {
                window.Raise(nameof(SidePanel));
                handlers?.Invoke(this, new RunicSidePanelClosedEventArgs(reason));
            });
        }

        private static ValueTask<RunicSidePanelRefusal?> Result(RunicSidePanelRefusal? refusal) => ValueTask.FromResult(refusal);
    }
}

/// <summary>A request to a <see cref="RunicTestWindowPresentation"/>, as <see cref="RunicTestWindowPresentation.Requests"/> records it.</summary>
/// <param name="Kind">Which request it was.</param>
public sealed record RunicWindowPresentationRequest(RunicWindowPresentationRequestKind Kind)
{
    /// <summary>Gets the value of <see cref="RunicWindowPresentationRequestKind.SetVisible"/>, <see cref="RunicWindowPresentationRequestKind.SetFullScreen"/> and <see cref="RunicWindowPresentationRequestKind.SetDocumentEdited"/>.</summary>
    public bool? Value { get; init; }

    /// <summary>Gets the scheme of <see cref="RunicWindowPresentationRequestKind.SetColorScheme"/>.</summary>
    public DesktopColorScheme? ColorScheme { get; init; }

    /// <summary>Gets the activation context of <see cref="RunicWindowPresentationRequestKind.Present"/>.</summary>
    public DesktopActivationContext? Activation { get; init; }

    /// <summary>Gets the options of <see cref="RunicWindowPresentationRequestKind.OpenSidePanel"/>.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public RunicSidePanelOptions? SidePanel { get; init; }

    /// <summary>Gets the edge of <see cref="RunicWindowPresentationRequestKind.OpenSidePanel"/> and <see cref="RunicWindowPresentationRequestKind.MoveSidePanel"/>, as requested.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public RunicSidePanelEdge? Edge { get; init; }

    /// <summary>Gets the width of <see cref="RunicWindowPresentationRequestKind.OpenSidePanel"/> and <see cref="RunicWindowPresentationRequestKind.SetSidePanelWidth"/>.</summary>
    /// <remarks>Experimental (<c>RUNICDESK104</c>).</remarks>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public int? Width { get; init; }
}

/// <summary>Which <see cref="IRunicWindowPresentation"/> request a <see cref="RunicWindowPresentationRequest"/> records.</summary>
public enum RunicWindowPresentationRequestKind
{
    /// <summary><see cref="IRunicWindowPresentation.PresentAsync"/>.</summary>
    Present,

    /// <summary><see cref="IRunicWindowPresentation.MinimizeAsync"/>.</summary>
    Minimize,

    /// <summary><see cref="IRunicWindowPresentation.SetVisibleAsync"/>.</summary>
    SetVisible,

    /// <summary><see cref="IRunicWindowPresentation.SetFullScreenAsync"/>.</summary>
    SetFullScreen,

    /// <summary><see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/>.</summary>
    SetDocumentEdited,

    /// <summary><see cref="IRunicWindowPresentation.SetColorSchemeAsync"/>.</summary>
    SetColorScheme,

    /// <summary><see cref="IRunicWindowPresentation.OpenSidePanelAsync"/>. Experimental (<c>RUNICDESK104</c>).</summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    OpenSidePanel,

    /// <summary><see cref="IRunicSidePanel.MoveToEdgeAsync"/>. Experimental (<c>RUNICDESK104</c>).</summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    MoveSidePanel,

    /// <summary><see cref="IRunicSidePanel.SetWidthAsync"/>. Experimental (<c>RUNICDESK104</c>).</summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    SetSidePanelWidth,

    /// <summary><see cref="IRunicSidePanel.FocusAsync"/>. Experimental (<c>RUNICDESK104</c>).</summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    FocusSidePanel,

    /// <summary><see cref="IRunicSidePanel.CloseAsync"/>, also by disposing it. Experimental (<c>RUNICDESK104</c>).</summary>
    [Experimental("RUNICDESK104", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    CloseSidePanel,
}

/// <summary>Registers <see cref="RunicTestWindowPresentation"/> for dependency injection.</summary>
public static class RunicTestWindowPresentationServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="RunicTestWindowPresentation"/> per scope as the host of the scope's
    /// <see cref="IRunicWindowPresentation"/>, replacing the registration of <c>AddRunicWindowService()</c>, so the
    /// application's own registration method can run unchanged in the test.
    /// </summary>
    /// <param name="services">The test's services.</param>
    /// <param name="immediate">
    /// <see langword="false"/>, the default, delivers the fake's changes as an application's Window does: in a turn of
    /// the scope's model context after the request completed, with handler failures logged, and
    /// <see cref="PlatformUnavailableReason.OwnerClosed"/> once the scope is disposed. <see langword="true"/> makes the
    /// fake itself the scope's presentation, which raises its changes at once on the calling thread.
    /// </param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddRunicTestWindowPresentation(this IServiceCollection services, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IRunicWindowPresentation>();
        services.TryAddScoped<RunicTestWindowPresentation>();
        if (immediate)
            services.AddScoped<IRunicWindowPresentation>(static provider => provider.GetRequiredService<RunicTestWindowPresentation>());
        else
            services.AddScoped<IRunicWindowPresentation>(static provider =>
            {
                // Read now: the scope that closes the presentation is disposed by then.
                var context = provider.GetService<IRunicModelContext>();
                var logger = provider.GetService<ILoggerFactory>()?.CreateLogger(RunicViewsTelemetry.LogCategory) ?? TraceFallbackLogger.Instance;
                return RunicWindowPresentation.Wrap(provider.GetRequiredService<RunicTestWindowPresentation>(), () => context, () => logger);
            });
        return services;
    }
}
