using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Navigation;
using Bridge = Runic.Application.Views;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// A region of a host page that shows a guest application's Window (the Runic host protocol, <c>region/1</c>): the
/// page's <c>&lt;runic-hosted-region name="…"&gt;</c> shows the guest's page in a frame from the guest's own loopback
/// origin, as part of the host's window.
/// </summary>
/// <remarks>
/// <para>
/// A policy's <see cref="RunicHostedWindowDecision.Embed(string, RunicHostedRegionOptions)"/> creates it with its
/// <see cref="RunicHostedWindow"/>. The window is open from then on, whether or not a region shows it: it
/// <see cref="State">waits</see> until a host page declares a region of its name, <see cref="RunicHostedRegionState.Loading">loads</see>
/// the guest's page there, and is <see cref="RunicHostedRegionState.Attached">attached</see> once that page connected.
/// When the page removes the region, reloads or navigates, the window is <see cref="RunicHostedRegionState.Detached">detached</see>
/// and stays open; it attaches again when a region of the same name appears in the same host window
/// (<see cref="RunicHostedRegionOptions.ReattachOnReload"/>). When the guest closes its Window or goes away, the region is
/// <see cref="RunicHostedRegionState.Ended">ended</see>, and the page shows its own content for it.
/// </para>
/// <para>
/// A departure the host decides, such as a wizard's Back or Next, asks the guest first: register
/// <see cref="AsDepartureGuard"/> with the step, or call <see cref="RequestCloseAsync"/>. The region's dialogs are owned
/// by its host window. Its events are raised on a thread-pool thread, one at a time.
/// </para>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedRegion
{
    internal const int MaximumNameLength = 64;
    internal const int MaximumTitleLength = 1_024;
    // How long after the host asked its page to focus the region a focus that the page reports as script's counts as
    // the host's programmatic focus (region-focus programmatic).
    internal static readonly TimeSpan ProgrammaticFocusWindow = TimeSpan.FromSeconds(1);
    // A guest that revokes its frame token this often within a minute stays detached (region-page revoked).
    private const int MaximumRevokesPerMinute = 3;

    private readonly RunicHostedApplication _application;
    private readonly HostedRegionDirectory _directory;
    private readonly string _allow;
    private readonly object _stateGate = new();
    private readonly object _queueGate = new();
    private Task _queue = Task.CompletedTask;

    // Under the directory's gate: the slot the window shows now, the host page it is bound to (the one it showed last),
    // the host window and name it waits for, and whether the host application detached it.
    private HostedRegionSlot? _slot;
    private IHostedRegionPage? _page;
    private IRunicWindow? _target;
    private string _name;
    private bool _hostDetached;
    private bool _attachedOnce;

    // Under _stateGate.
    private RunicHostedRegionState _state = RunicHostedRegionState.Waiting;
    // The slot whose frame the guest's attach is for, from its region-attach reply until its region-detach.
    private HostedRegionSlot? _attachedSlot;
    private bool _ended;
    private RunicHostedRegionSize? _size;
    private RunicHostedRegionSize? _sentSize;
    private int? _preferredWidth;
    private int? _preferredHeight;
    private string? _title;
    private bool _documentEdited;
    private bool _visible;
    private bool _focused;
    // The focus entered the region by the user's Tab, Shift+Tab or click, not by the host's or the guest's script.
    private bool _focusByUser;
    private long _programmaticFocus;
    private bool _userActive;
    private long _userActiveEnded;
    private (bool Active, bool Visible, bool Minimized)? _sentActivity;
    private DesktopWindow? _observedWindow;
    private readonly Queue<long> _revokes = new();

    // set-title per window (host-protocol.md, Limits): 10 a second after a burst of 10.
    internal RequestRate TitleRate { get; } = new(10, TimeSpan.FromSeconds(1));

    internal RunicHostedRegion(RunicHostedApplication application, HostedRegionDirectory directory, RunicHostedWindow window, string name,
        IRunicWindow? hostWindow, RunicHostedRegionOptions options, string allow, bool component = false)
    {
        IsComponent = component;
        _application = application;
        _directory = directory;
        Window = window;
        _name = name;
        _target = hostWindow;
        Options = options;
        _allow = allow;
        _title = null;
    }

    /// <summary>The name of the region that shows the window, or that it waits for.</summary>
    public string Name
    {
        get { lock (_directory.Gate) return _name; }
    }

    /// <summary>
    /// The host's Window whose page shows the region, or showed it last; <see langword="null"/> before a page declared it,
    /// or for a page of no Runic Window.
    /// </summary>
    public IRunicWindow? HostWindow
    {
        get { lock (_directory.Gate) return _page?.Window ?? _target; }
    }

    /// <summary>The guest's window that the region shows.</summary>
    public RunicHostedWindow Window { get; }

    /// <summary>How the region shows the window, as the policy decided.</summary>
    public RunicHostedRegionOptions Options { get; }

    /// <summary>
    /// Whether the region shows the window as a component in the host page's own document
    /// (<see cref="RunicHostedWindowDecision.EmbedComponent(string)"/>), not in a frame.
    /// </summary>
    public bool IsComponent { get; }

    /// <summary>Where the window is: waiting for a region, loading, attached, detached or ended.</summary>
    public RunicHostedRegionState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <summary>The region's size and scale as the host page reported it last, or <see langword="null"/> before its first report.</summary>
    public RunicHostedRegionSize? Size
    {
        get { lock (_stateGate) return _size; }
    }

    /// <summary>The width the guest asked for last (<c>resize</c>), in CSS pixels, or <see langword="null"/>.</summary>
    public int? PreferredWidth
    {
        get { lock (_stateGate) return _preferredWidth; }
    }

    /// <summary>
    /// The height the guest asked for last (<c>resize</c>), in CSS pixels, or <see langword="null"/>; a region whose page
    /// declares <c>sizing="content-height"</c> takes it, within <see cref="RunicHostedRegionOptions.MinimumHeight"/> and
    /// <see cref="RunicHostedRegionOptions.MaximumHeight"/>.
    /// </summary>
    public int? PreferredHeight
    {
        get { lock (_stateGate) return _preferredHeight; }
    }

    /// <summary>The title of the guest's page (<c>set-title</c>), the frame's accessible name, or <see langword="null"/>.</summary>
    public string? Title
    {
        get { lock (_stateGate) return _title; }
    }

    /// <summary>Whether the guest marks its Window's document as edited (<c>set-document-edited</c>).</summary>
    public bool IsDocumentEdited
    {
        get { lock (_stateGate) return _documentEdited; }
    }

    /// <summary>Whether the keyboard focus is in the region, as the host page reported it last.</summary>
    public bool IsFocused
    {
        get { lock (_stateGate) return _attachedSlot is not null && _focused; }
    }

    /// <summary>Raised when a region of a host page starts to show the window, which then loads the guest's page.</summary>
    public event EventHandler<RunicHostedRegionEventArgs>? Attached;

    /// <summary>
    /// Raised when the region no longer shows the window, with the reason (<see cref="RunicHostedRegionEventArgs.Reason"/>):
    /// <c>removed</c>, <c>reload</c>, <c>crashed</c>, <c>host</c> or <c>closed</c>.
    /// </summary>
    public event EventHandler<RunicHostedRegionEventArgs>? Detached;

    /// <summary>Raised when <see cref="State"/> changed.</summary>
    public event EventHandler<RunicHostedRegionEventArgs>? StateChanged;

    /// <summary>Raised when <see cref="Title"/> changed.</summary>
    public event EventHandler<RunicHostedRegionEventArgs>? TitleChanged;

    /// <summary>Raised when <see cref="IsDocumentEdited"/> changed.</summary>
    public event EventHandler<RunicHostedRegionEventArgs>? DocumentEditedChanged;

    /// <summary>Raised when <see cref="PreferredWidth"/> or <see cref="PreferredHeight"/> changed.</summary>
    public event EventHandler<RunicHostedRegionEventArgs>? PreferredSizeChanged;

    /// <summary>
    /// Asks the guest to close its window, as a departure the host decides does: a Window that guards its close decides
    /// first (<see cref="RunicHostedWindow.RequestCloseAsync"/>).
    /// </summary>
    /// <param name="force">Whether the window closes whatever the guest decides.</param>
    /// <param name="cancellationToken">Cancels the wait for the guest's decision.</param>
    /// <returns>Whether the guest allowed the close; <see langword="true"/> for an ended region.</returns>
    public ValueTask<bool> RequestCloseAsync(bool force = false, CancellationToken cancellationToken = default) =>
        Window.RequestCloseAsync(force, cancellationToken);

    /// <summary>
    /// A departure guard (<see cref="INavigationDepartureGuard"/>) that asks the guest's close guard first, such as for a
    /// wizard step that shows the region: the departure goes ahead only when the guest allowed the close and its Window
    /// closed, or the region has ended.
    /// </summary>
    /// <returns>The guard; it may run again for the same departure.</returns>
    public INavigationDepartureGuard AsDepartureGuard() => new DepartureGuard(this);

    /// <summary>
    /// Detaches the window from its region; it stays open and detached, the region shows another window that waits for it
    /// or its page's fallback content, and <see cref="MoveToAsync"/> attaches it again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait; the detach still happens.</param>
    /// <returns>A task that completes when the guest was told.</returns>
    public Task DetachAsync(CancellationToken cancellationToken = default) =>
        Enqueue(async () =>
        {
            _ = _directory.Detach(this);
            await DetachGuestAsync("host").ConfigureAwait(false);
        }).WaitAsync(cancellationToken);

    /// <summary>
    /// Shows the window in the region <paramref name="region"/> of the page of <paramref name="window"/>, such as another
    /// step of a wizard: a window that a region shows now is detached first. It attaches once the page declares the region
    /// and no other window shows there.
    /// </summary>
    /// <param name="window">The host's Window whose page declares the region.</param>
    /// <param name="region">The region's name.</param>
    /// <param name="cancellationToken">Cancels the wait; the move still happens.</param>
    /// <returns>A task that completes when the move started: the window is detached, and attaches when the region takes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="region"/> is not a region name.</exception>
    public Task MoveToAsync(IRunicWindow window, string region, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        RequireName(region, nameof(region));
        return Enqueue(async () =>
        {
            if (IsEnded) return;
            _ = _directory.Detach(this);
            await DetachGuestAsync("host").ConfigureAwait(false);
            // The attach follows in the queue; the move has started once the guest learned of the detach.
            if (_directory.Retarget(this, window, region) is { } slot) OnBound(slot);
        }).WaitAsync(cancellationToken);
    }

    internal static string RequireName(string name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        if (!IsName(name))
            throw new ArgumentException($"'{name}' is not a region name: 1 to {MaximumNameLength} ASCII letters, digits, dots, underscores or hyphens.", parameter);
        return name;
    }

    internal static bool IsName(string? name) =>
        name is { Length: > 0 and <= MaximumNameLength } && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // ----- The directory's side (under its gate)

    internal IRunicWindow? HostWindowTarget => _target;

    internal HostedRegionSlot? SlotLocked => _slot;

    internal IHostedRegionPage? PageLocked => _page;

    internal bool HostDetachedLocked
    {
        get => _hostDetached;
        set => _hostDetached = value;
    }

    // Whether the window may show in a region of this page: the page it showed last; else the host window it waits for,
    // or any for a name-only decision. A window detached by its page comes back only with ReattachOnReload.
    internal bool Accepts(IHostedRegionPage page)
    {
        if (_ended) return false;
        if (_page is not null) return ReferenceEquals(_page, page) && (!_attachedOnce || Options.ReattachOnReload);
        return _target is null || ReferenceEquals(_target, page.Window);
    }

    internal void BindLocked(HostedRegionSlot slot)
    {
        _slot = slot;
        _page = slot.Page;
        slot.Region = this;
    }

    internal void UnbindLocked(HostedRegionSlot slot)
    {
        if (ReferenceEquals(_slot, slot)) _slot = null;
        if (ReferenceEquals(slot.Region, this)) slot.Region = null;
    }

    internal void RetargetLocked(IRunicWindow window, string name)
    {
        _target = window;
        _name = name;
        _page = null;
        _hostDetached = false;
        _attachedOnce = false;
    }

    private bool IsCurrent(HostedRegionSlot slot)
    {
        lock (_directory.Gate) return ReferenceEquals(_slot, slot);
    }

    private HostedRegionSlot? CurrentSlot
    {
        get { lock (_directory.Gate) return _slot; }
    }

    // ----- Events from the directory

    // A page's region takes the window: it attaches, after the window-open reply when the window is still opening.
    internal void OnBound(HostedRegionSlot slot) => _ = Enqueue(() => AttachAsync(slot));

    // The page removed the region, reloaded, or its host window closed: the guest is told, the window stays open.
    internal void OnSlotRemoved(HostedRegionSlot slot, string reason) => _ = Enqueue(async () =>
    {
        bool shown;
        lock (_stateGate) shown = ReferenceEquals(_attachedSlot, slot);
        if (shown) await DetachGuestAsync(reason).ConfigureAwait(false);
        // A window that waited for the guest's reply when the page removed the region: loading no more.
        else if (CurrentSlot is null) SetStateIf(RunicHostedRegionState.Loading, RunicHostedRegionState.Detached);
    });

    // The host window closed: the window closes too, after the guest learned of the detach (host-protocol.md, Lifecycle
    // in a region).
    internal void OnHostWindowClosed() => _ = Enqueue(async () =>
    {
        await Window.PushesSent().ConfigureAwait(false);
        _ = await Window.RequestCloseAsync(force: true).ConfigureAwait(false);
    });

    // hosted-region-state: the region's size, visibility and focus.
    internal void OnReport(HostedRegionSlot slot, Bridge.HostedRegionReport report)
    {
        string? focusDirection = null;
        bool bounds;
        lock (_stateGate)
        {
            if (_ended) return;
            _size = SizeOf(report);
            var current = ReferenceEquals(_attachedSlot, slot);
            bounds = current && _sentSize != _size;
            if (report.Focused && !_focused)
            {
                _focusByUser = report.FocusSource is Bridge.HostedRegionFocusSource.Tab or Bridge.HostedRegionFocusSource.ShiftTab
                    or Bridge.HostedRegionFocusSource.Pointer;
                focusDirection = report.FocusSource switch
                {
                    Bridge.HostedRegionFocusSource.Tab => "forward",
                    Bridge.HostedRegionFocusSource.ShiftTab => "backward",
                    Bridge.HostedRegionFocusSource.Script when _programmaticFocus != 0 &&
                        Stopwatch.GetElapsedTime(_programmaticFocus) <= ProgrammaticFocusWindow => "programmatic",
                    _ => null,
                };
                _programmaticFocus = 0;
                if (!current) focusDirection = null;
            }
            else if (!report.Focused) _focusByUser = false;
            _focused = report.Focused;
            _visible = report.Visible;
        }
        if (bounds) ScheduleBounds();
        if (focusDirection is { } direction)
            Window.Push("region-focus", () => new JsonObject { ["kind"] = "region-focus", ["window"] = Window.Id, ["focus"] = "enter", ["direction"] = direction });
        // The window services' rule reads the focus at once (RunicHostedWindow.Activation).
        _ = Window.Activation;
        PushActivity();
    }

    // region-page (guest → host): the guest's page in the frame connected, lost its connection, or the guest revoked its
    // token after a launch conflict.
    internal void OnPage(string page)
    {
        switch (page)
        {
            case "connected":
                lock (_stateGate) if (_attachedSlot is null || _state != RunicHostedRegionState.Loading) return;
                SetState(RunicHostedRegionState.Attached);
                break;
            case "disconnected":
                lock (_stateGate) if (_attachedSlot is null || _state != RunicHostedRegionState.Attached) return;
                SetState(RunicHostedRegionState.Loading);
                break;
            case "revoked":
                _ = Enqueue(async () =>
                {
                    HostedRegionSlot? slot;
                    lock (_stateGate) slot = _attachedSlot;
                    if (slot is null) return;
                    await DetachGuestAsync("host").ConfigureAwait(false);
                    if (slot is null || !IsCurrent(slot)) return;
                    bool again;
                    lock (_stateGate)
                    {
                        var now = Stopwatch.GetTimestamp();
                        while (_revokes.Count > 0 && Stopwatch.GetElapsedTime(_revokes.Peek(), now) > TimeSpan.FromMinutes(1)) _revokes.Dequeue();
                        _revokes.Enqueue(now);
                        again = _revokes.Count <= MaximumRevokesPerMinute;
                    }
                    if (again) await AttachAsync(slot).ConfigureAwait(false);
                    else slot.Page.Empty(slot.Name, Bridge.HostedRegionEmptyReason.Detached);
                });
                break;
        }
    }

    // ----- Attach and detach (host-protocol.md, region-attach and region-detach), one at a time and in order.

    private Task Enqueue(Func<Task> work)
    {
        lock (_queueGate)
        {
            var next = _queue.ContinueWith(async _ =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { _application.LogRegionFailed(error); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _queue = next;
            return next;
        }
    }

    // Waits for the window-open reply first: a region-attach for a window the guest does not know yet is refused.
    internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AttachAsync(HostedRegionSlot slot)
    {
        await Opened.Task.ConfigureAwait(false);
        if (IsEnded || !IsCurrent(slot)) return;
        HostedRegionSlot? attached;
        lock (_stateGate) attached = _attachedSlot;
        if (ReferenceEquals(attached, slot)) return;
        // Shown in another region still: the guest is told first (host-protocol.md, region-attach, One at a time).
        if (attached is not null) await DetachGuestAsync("host").ConfigureAwait(false);
        SetState(RunicHostedRegionState.Loading);
        ObserveHostWindow(slot.Page.NativeWindow);
        var size = SizeOf(slot.Report);
        // The window's pushes so far, such as the activity of a detach, reach the guest before the attach.
        await Window.PushesSent().ConfigureAwait(false);
        var attach = await _application.RegionAttachAsync(Window, slot.Page.Embedder, size, slot.Sizing, slot.Page.AllowsSameSite,
            IsComponent).ConfigureAwait(false);
        if (attach.Url is not { } url)
        {
            // The guest refused or failed the attach: the window stays open and unattached; the page shows its fallback.
            _application.LogRegionAttachFailed(Name, attach.Failure ?? "failed");
            lock (_directory.Gate)
            {
                UnbindLocked(slot);
                _attachedOnce = true;
            }
            SetState(_ended ? RunicHostedRegionState.Ended : RunicHostedRegionState.Detached);
            slot.Page.Empty(slot.Name, Bridge.HostedRegionEmptyReason.Detached);
            return;
        }
        // A component from the host page's own site is the rule for an embedded host (host-protocol.md, Component mode).
        if (attach.SameSite && attach.Component is null) _application.LogRegionSameSite(Name);
        lock (_stateGate)
        {
            _attachedSlot = slot;
            _sentSize = size;
            // The region as the page reported it last, before the frame showed the guest's page.
            if (slot.Report is { } report)
            {
                _size = SizeOf(report);
                _visible = report.Visible;
                _focused = report.Focused;
                _focusByUser = false;
            }
        }
        lock (_directory.Gate) _attachedOnce = true;
        // The page removed the region while the guest answered: the detach that follows in the queue tells the guest.
        if (!IsCurrent(slot)) return;
        string? title;
        lock (_stateGate) title = _title;
        try
        {
            _ = attach.Component is { } component
                ? slot.Page.ShowComponent(slot.Name, component.Module, url, component.Token)
                : slot.Page.ShowFrame(slot.Name, url, title, _allow);
        }
        catch (ArgumentException error) { _application.LogRegionFailed(error); }
        Raise(Attached, null);
        _application.RaiseRegionAttached(this);
        // A size the page reported while the guest answered.
        ScheduleBounds();
        ApplyPreferredHeight(slot);
        PushActivity();
        if (Options.FocusOnAttach) RequestFocus();
    }

    private async Task DetachGuestAsync(string reason)
    {
        bool attached;
        lock (_stateGate)
        {
            attached = _attachedSlot is not null;
            _attachedSlot = null;
            _focused = false;
            _focusByUser = false;
        }
        if (!attached)
        {
            SetStateIf(RunicHostedRegionState.Loading, RunicHostedRegionState.Detached);
            return;
        }
        await _application.PushAsync(new JsonObject { ["kind"] = "region-detach", ["window"] = Window.Id, ["reason"] = reason }).ConfigureAwait(false);
        SetState(IsEnded ? RunicHostedRegionState.Ended : RunicHostedRegionState.Detached);
        Raise(Detached, reason);
        _application.RaiseRegionDetached(this, reason);
        PushActivity();
    }

    // The window closed: the region is ended and empty (host-protocol.md, Lifecycle in a region). Its frame goes with
    // the window; the guest needs no region-detach.
    internal Task EndAsync()
    {
        lock (_stateGate)
        {
            if (_ended) return Task.CompletedTask;
            _ended = true;
        }
        Opened.TrySetResult();
        _directory.Close(this);
        return Enqueue(() =>
        {
            bool attached;
            lock (_stateGate)
            {
                attached = _attachedSlot is not null;
                _attachedSlot = null;
            }
            ObserveHostWindow(null);
            SetState(RunicHostedRegionState.Ended);
            if (attached)
            {
                Raise(Detached, "closed");
                _application.RaiseRegionDetached(this, "closed");
            }
            return Task.CompletedTask;
        });
    }

    internal bool IsEnded
    {
        get { lock (_stateGate) return _ended; }
    }

    // ----- What the window does in its region

    // The host window that shows the region now, which owns the window's dialogs and context menus; null while detached.
    internal DesktopWindow? NativeWindow
    {
        get
        {
            lock (_stateGate) return _attachedSlot?.Page.NativeWindow;
        }
    }

    // The host window that shows the region, or showed it last: the owner of the window's owned windows.
    internal DesktopWindow? LastNativeWindow
    {
        get { lock (_directory.Gate) return _page?.NativeWindow; }
    }

    // The window-open reply's region member: the size of the region that takes the window, or null while it waits.
    internal JsonNode? OpenRegionMember()
    {
        var report = CurrentSlot?.Report;
        if (report is null) return null;
        var size = SizeOf(report);
        return new JsonObject { ["width"] = size.Width, ["height"] = size.Height, ["scale"] = size.Scale };
    }

    // present, or the host's own decision: the page moves the focus into the region, only when it holds none (host-protocol.md,
    // Focus in a region); the page's next report tells whether it did, and region-focus programmatic follows then.
    internal bool RequestFocus()
    {
        HostedRegionSlot? slot;
        lock (_stateGate)
        {
            slot = _attachedSlot;
            if (slot is null) return false;
            _programmaticFocus = Stopwatch.GetTimestamp();
        }
        return slot.Page.Focus(slot.Name);
    }

    internal void SetTitle(string? title)
    {
        lock (_stateGate)
        {
            if (_title == title) return;
            _title = title;
        }
        if (CurrentSlot is { } slot)
        {
            try { _ = slot.Page.Update(slot.Name, Bridge.HostedRegionTitle.Set(title), null); }
            catch (ArgumentException error) { _application.LogRegionFailed(error); }
        }
        Raise(TitleChanged, null);
    }

    internal void SetDocumentEdited(bool edited)
    {
        lock (_stateGate)
        {
            if (_documentEdited == edited) return;
            _documentEdited = edited;
        }
        Raise(DocumentEditedChanged, null);
    }

    // resize in a region: the guest's preferred size. True when the region follows the guest's height.
    internal bool SetPreferredSize(int width, int height)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _preferredWidth != width || _preferredHeight != height;
            _preferredWidth = width;
            _preferredHeight = height;
        }
        if (changed) Raise(PreferredSizeChanged, null);
        HostedRegionSlot? attached;
        lock (_stateGate) attached = _attachedSlot;
        return attached is { Sizing: Bridge.HostedRegionSizing.ContentHeight } && ApplyPreferredHeight(attached);
    }

    private bool ApplyPreferredHeight(HostedRegionSlot slot)
    {
        int? height;
        lock (_stateGate)
        {
            if (slot.Sizing != Bridge.HostedRegionSizing.ContentHeight || !ReferenceEquals(_attachedSlot, slot)) return false;
            height = _preferredHeight;
        }
        if (height is not { } preferred) return true;
        var clamped = Math.Clamp(preferred, Options.MinimumHeight ?? 0, Math.Max(Options.MinimumHeight ?? 0, Options.MaximumHeight ?? Bridge.BridgeHostedRegions.MaximumSize));
        try { _ = slot.Page.Update(slot.Name, default, clamped); }
        catch (ArgumentException error) { _application.LogRegionFailed(error); }
        return true;
    }

    // A context menu's point, in CSS pixels of the frame's viewport, in the host page's viewport: inside the region, a
    // point outside it at its nearest edge. Null when the region has no report.
    internal (double X, double Y)? PagePoint(double x, double y)
    {
        HostedRegionSlot? slot;
        lock (_stateGate) slot = _attachedSlot;
        if (slot?.Report is not { } report) return null;
        return (report.X + Math.Clamp(x, 0, Math.Max(0, report.Width)), report.Y + Math.Clamp(y, 0, Math.Max(0, report.Height)));
    }

    // The window services' rule in a region (host-protocol.md, Features in a region): the user works in the window while
    // the host window is active, not by the guest's request, and the focus is in the region, which the user moved there.
    internal RunicHostedWindow.ActivationSnapshot Combine(RunicHostedWindow.ActivationSnapshot host)
    {
        lock (_stateGate)
        {
            var focused = _attachedSlot is not null && _focused;
            var active = host.Active && focused;
            var guest = active && (host.GuestActivated || !_focusByUser);
            var user = active && !guest;
            if (_userActive && !user) _userActiveEnded = Stopwatch.GetTimestamp();
            _userActive = user;
            var grace = !user && _userActiveEnded != 0 && Stopwatch.GetElapsedTime(_userActiveEnded) <= RunicHostedWindow.ClipboardWriteGrace;
            return new RunicHostedWindow.ActivationSnapshot(active, guest, grace);
        }
    }

    // The activity push in a region (host-protocol.md, Presentation of a window).
    internal JsonObject ActivityMembers()
    {
        var (active, visible, minimized) = CurrentActivity();
        return new JsonObject { ["active"] = active, ["minimized"] = minimized, ["visible"] = visible };
    }

    private (bool Active, bool Visible, bool Minimized) CurrentActivity()
    {
        var native = NativeWindow;
        var activity = native?.Activity;
        lock (_stateGate)
            return (_attachedSlot is not null && _focused && activity is { IsActive: true }, _attachedSlot is not null && _visible, activity is { IsMinimized: true });
    }

    private void PushActivity()
    {
        var now = CurrentActivity();
        lock (_stateGate)
        {
            if (_sentActivity == now) return;
            _sentActivity = now;
        }
        Window.Push("activity", () =>
        {
            var push = ActivityMembers();
            push.Insert(0, "window", Window.Id);
            push.Insert(0, "kind", "activity");
            return push;
        });
    }

    // The host window's activation counts for the window services and the activity push.
    private void ObserveHostWindow(DesktopWindow? window)
    {
        DesktopWindow? previous;
        lock (_stateGate)
        {
            previous = _observedWindow;
            if (ReferenceEquals(previous, window)) return;
            _observedWindow = window;
        }
        if (previous is not null) previous.ActivityChanged -= OnHostActivity;
        if (window is not null) window.ActivityChanged += OnHostActivity;
        Window.FollowHostWindow(window);
    }

    private void OnHostActivity(object? sender, DesktopWindowActivityChangedEventArgs e)
    {
        _ = Window.Activation;
        PushActivity();
    }

    // region-bounds: coalesced per window, latest wins, at most one per animation frame of the host page.
    private int _boundsDue;
    internal static readonly TimeSpan BoundsFrame = TimeSpan.FromMilliseconds(16);

    private void ScheduleBounds()
    {
        if (Interlocked.Exchange(ref _boundsDue, 1) == 1) return;
        _ = Task.Delay(BoundsFrame).ContinueWith(_ =>
        {
            Volatile.Write(ref _boundsDue, 0);
            bool send;
            lock (_stateGate)
            {
                send = _attachedSlot is not null && _size is { } size && size != _sentSize;
                if (send) _sentSize = _size;
            }
            if (send) Window.Push("region-bounds", () => BoundsPush());
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private JsonObject BoundsPush()
    {
        RunicHostedRegionSize size;
        lock (_stateGate) size = _sentSize ?? new RunicHostedRegionSize(0, 0, 1);
        return new JsonObject { ["kind"] = "region-bounds", ["window"] = Window.Id, ["width"] = size.Width, ["height"] = size.Height, ["scale"] = size.Scale };
    }

    // The region's size as region-attach and region-bounds carry it: integers from 0 to 32,767 and a scale from 0.25 to 8.
    private static RunicHostedRegionSize SizeOf(Bridge.HostedRegionReport? report) => report is null
        ? new RunicHostedRegionSize(0, 0, 1)
        : new RunicHostedRegionSize(
            (int)Math.Clamp(Math.Round(report.Width), 0, Bridge.BridgeHostedRegions.MaximumSize),
            (int)Math.Clamp(Math.Round(report.Height), 0, Bridge.BridgeHostedRegions.MaximumSize),
            Math.Clamp(report.Scale, Bridge.BridgeHostedRegions.MinimumScale, Bridge.BridgeHostedRegions.MaximumScale));

    private void SetStateIf(RunicHostedRegionState from, RunicHostedRegionState to)
    {
        lock (_stateGate)
        {
            if (_state != from) return;
            _state = to;
        }
        Raise(StateChanged, null);
    }

    private void SetState(RunicHostedRegionState state)
    {
        lock (_stateGate)
        {
            if (_state == state) return;
            _state = state;
        }
        Raise(StateChanged, null);
    }

    private void Raise(EventHandler<RunicHostedRegionEventArgs>? handler, string? reason)
    {
        if (handler is null) return;
        _application.Post(() => handler(this, new RunicHostedRegionEventArgs(this, reason)));
    }

    private sealed class DepartureGuard(RunicHostedRegion region) : INavigationDepartureGuard
    {
        public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
        {
            var window = region.Window;
            if (!window.IsOpen) return true;
            if (!await window.RequestCloseAsync(force: false, cancellationToken).ConfigureAwait(false)) return false;
            // The guest allowed: the departure waits until its Window closed, as a user's close of a window does.
            try { await window.Closed.WaitAsync(region._application.StopTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            return true;
        }
    }
}

/// <summary>Where a <see cref="RunicHostedRegion"/>'s window is.</summary>
/// <remarks>Open: a later release can add states. Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicHostedRegionState
{
    /// <summary>No host page declares a region for the window yet.</summary>
    Waiting,

    /// <summary>A region shows the window, and the guest's page loads or reconnects.</summary>
    Loading,

    /// <summary>A region shows the window, and the guest's page is connected.</summary>
    Attached,

    /// <summary>The window is open, but no region shows it: the page removed or reloaded the region, or the host detached it.</summary>
    Detached,

    /// <summary>The window closed, or its guest went away; the region shows its page's own content.</summary>
    Ended,
}

/// <summary>A region's size, in CSS pixels of the host page, and the page's device pixel ratio.</summary>
/// <param name="Width">The width, from 0 to 32,767.</param>
/// <param name="Height">The height, from 0 to 32,767.</param>
/// <param name="Scale">The device pixel ratio, from 0.25 to 8.</param>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public readonly record struct RunicHostedRegionSize(int Width, int Height, double Scale);

/// <summary>How a region shows a guest's window (<see cref="RunicHostedWindowDecision.Embed(string, RunicHostedRegionOptions)"/>).</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedRegionOptions
{
    /// <summary>The defaults.</summary>
    public static RunicHostedRegionOptions Default { get; } = new();

    /// <summary>
    /// The permissions of the region's frame, or <see langword="null"/>, the default, for those the guest's page asks for
    /// that the host grants its own windows (<see cref="RunicHostedWindowRequest.WindowOptions"/>), as a window of its own
    /// gets them. <see cref="DesktopPermissionGrant.MediaCapture"/> allows the camera and the microphone.
    /// </summary>
    public DesktopPermissionGrant? Permissions { get; init; }

    /// <summary>The least height, in CSS pixels, of a region whose page declares <c>sizing="content-height"</c>; 0 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 0 to 32,767.</exception>
    public int? MinimumHeight
    {
        get;
        init => field = RequireHeight(value, nameof(MinimumHeight));
    }

    /// <summary>The greatest height, in CSS pixels, of a region whose page declares <c>sizing="content-height"</c>; 32,767 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 0 to 32,767.</exception>
    public int? MaximumHeight
    {
        get;
        init => field = RequireHeight(value, nameof(MaximumHeight));
    }

    /// <summary>
    /// Whether the window attaches again when a region of the same name appears again in the host window that showed it,
    /// as after its page reloaded or navigated back; <see langword="true"/> by default. Without it, a window its page
    /// detached stays detached until <see cref="RunicHostedRegion.MoveToAsync"/>.
    /// </summary>
    public bool ReattachOnReload { get; init; } = true;

    /// <summary>
    /// Whether the host page moves the keyboard focus into the region when it attaches, such as for a wizard step that
    /// consists of the region; <see langword="false"/> by default. The page moves it only when it holds no focus of its
    /// own, and the focus counts as the host's, not the user's, for the guest's window services.
    /// </summary>
    public bool FocusOnAttach { get; init; }

    private static int? RequireHeight(int? value, string name)
    {
        if (value is { } height) ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)height, (uint)Bridge.BridgeHostedRegions.MaximumSize, name);
        return value;
    }
}

/// <summary>A region and, for <see cref="RunicHostedRegion.Detached"/>, why it no longer shows its window.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedRegionEventArgs : EventArgs
{
    /// <summary>Creates the arguments for a region.</summary>
    /// <param name="region">The region.</param>
    /// <param name="reason">Why it detached, or <see langword="null"/>.</param>
    public RunicHostedRegionEventArgs(RunicHostedRegion region, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        Region = region;
        Reason = reason;
    }

    /// <summary>The region.</summary>
    public RunicHostedRegion Region { get; }

    /// <summary>
    /// For a detach, why: <c>removed</c> (the page removed the region), <c>reload</c> (the page reloaded, navigated or
    /// lost its connection), <c>crashed</c>, <c>host</c> (the host application detached or moved it) or <c>closed</c>
    /// (the window closed or its guest went away). An open set. <see langword="null"/> otherwise.
    /// </summary>
    public string? Reason { get; }
}

/// <summary>How a guest's window shows (<see cref="RunicHostedWindow.PresentationKind"/>).</summary>
/// <remarks>Open: a later release can add kinds. Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicHostedWindowPresentationKind
{
    /// <summary>In a native window of its own (<see cref="RunicHostedWindow.Presentation"/>).</summary>
    Window,

    /// <summary>In a region of a host page (<see cref="RunicHostedWindow.Region"/>).</summary>
    Region,
}
