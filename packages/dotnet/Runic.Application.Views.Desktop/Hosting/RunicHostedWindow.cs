using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// One Window of a guest application that the host shows (the Runic host protocol, <c>runic-host/1</c>): in a native
/// window of the host (window mode), or in a region of one of the host's pages (<c>region/1</c>, <see cref="Region"/>).
/// </summary>
/// <remarks>
/// <para>
/// The host owns the window's frame, placement and activation; the guest owns its page, which the window loads from the
/// guest's loopback address, and decides about its close. <see cref="Presentation"/> is the native window: the host can
/// place, present and observe it as any of its windows. A user's close, and <see cref="RequestCloseAsync"/>, ask the guest
/// first; the window closes when the guest's Window has closed, and <see cref="Closed"/> completes.
/// </para>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source has no timer; it holds nothing to release.")]
public sealed class RunicHostedWindow
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The guest's window-closed push arrived, or the guest is gone: nothing asks it about the close any more.
    private readonly TaskCompletionSource _guestClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _pushGate = new();
    private readonly List<(string Kind, Func<JsonObject> Push)> _pendingPushes = [];
    private bool _pushing;
    private DesktopWindow? _presentation;

    internal RunicHostedWindow(RunicHostedApplication application, string id, string type, RunicHostedWindow? owner, bool isModal,
        bool guardsClose)
    {
        Application = application;
        Id = id;
        Type = type;
        Owner = owner;
        IsModal = isModal;
        GuardsClose = guardsClose;
    }

    /// <summary>The guest application that the window belongs to.</summary>
    public RunicHostedApplication Application { get; }

    /// <summary>The guest's id for the window, unique for the guest.</summary>
    public string Id { get; }

    /// <summary>The guest's Window type, such as <c>MyApp.DocumentWindow`1[Report]</c>, or an empty string. Informational.</summary>
    public string Type { get; }

    /// <summary>The window of the same guest that owns this one, or <see langword="null"/>.</summary>
    public RunicHostedWindow? Owner { get; }

    /// <summary>Whether the window is modal to <see cref="Owner"/>.</summary>
    public bool IsModal { get; }

    /// <summary>Whether the guest decides about the window's close.</summary>
    public bool GuardsClose { get; }

    /// <summary>The host's native window that shows the guest's page.</summary>
    /// <exception cref="InvalidOperationException">
    /// The window has not opened yet, or it shows in a region (<see cref="PresentationKind"/>), which has no native window
    /// of its own: see <see cref="Region"/>.
    /// </exception>
    public DesktopWindow Presentation => Region is not null
        ? throw new InvalidOperationException("The window shows in a region of a host page, which has no native window of its own; see Region.")
        : _presentation ?? throw new InvalidOperationException("The window has not opened yet.");

    /// <summary>Whether the window shows in a native window of its own or in a region of a host page.</summary>
    public RunicHostedWindowPresentationKind PresentationKind => Region is null ? RunicHostedWindowPresentationKind.Window : RunicHostedWindowPresentationKind.Region;

    /// <summary>The region that shows the window (<c>region/1</c>), or <see langword="null"/> for a window of its own.</summary>
    public RunicHostedRegion? Region { get; private set; }

    /// <summary>
    /// The title of the guest's page in a region (<c>set-title</c>), or <see langword="null"/>; in a window of its own the
    /// native window shows the page's title itself.
    /// </summary>
    public string? Title => Region?.Title;

    /// <summary>Whether the guest marks the Window's document as edited (<c>set-document-edited</c>).</summary>
    public bool IsDocumentEdited => Region?.IsDocumentEdited ?? Volatile.Read(ref _documentEdited);

    private bool _documentEdited;

    internal void NoteDocumentEdited(bool edited) => Volatile.Write(ref _documentEdited, edited);

    /// <summary>Whether the window is open; a window in a region is open whether or not a region shows it now.</summary>
    public bool IsOpen => !_closed.Task.IsCompleted && (Region is { } region ? !region.IsEnded : _presentation is { IsOpen: true });

    /// <summary>Completes when the window has closed, for any reason.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// The guest's menus that the window shows now (<c>menus/1</c>), as the host's policy decided them; empty when the
    /// guest has none, the policy refused them or the feature is not in use.
    /// </summary>
    /// <remarks>
    /// A guest's item runs the guest's command, through the window's <see cref="DesktopWindow.MenuCommands"/>: choose it
    /// with <see cref="DesktopWindow.TryExecuteMenuItemAsync"/>, as the native bar and its shortcuts do. On Windows and
    /// Linux the window shows them as its own bar; on macOS they join the application menu bar while the window is key.
    /// </remarks>
    public IReadOnlyList<DesktopMenu> MenuBar => Volatile.Read(ref _menuBar);

    private IReadOnlyList<DesktopMenu> _menuBar = [];

    // menus/1: the guest's bar as it sent it, with its updates; null for none. Read and changed under MenuModel.
    internal JsonArray? GuestMenuBar { get; set; }

    internal readonly object MenuModel = new();

    // menus/1 limits (host-protocol.md, Limits): the rate of the window's menu requests, and its native bar's rebuilds,
    // coalesced; set by the application when the window opens.
    internal RequestRate? MenuRate { get; set; }

    internal CoalescedRebuild<MenuBarAnswer>? MenuRebuild { get; set; }

    // The guest's item ids of the bar shown now, which menu-invoke may name.
    private HashSet<string> _menuItems = new(StringComparer.Ordinal);

    internal void ShowMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Collect(menus, ids);
        Volatile.Write(ref _menuItems, ids);
        Volatile.Write(ref _menuBar, menus);

        static void Collect(IEnumerable<DesktopMenuEntry> entries, HashSet<string> ids)
        {
            foreach (var entry in entries)
            {
                if (entry is DesktopMenuItem item && HostedWire.ItemId(item) is { } id) ids.Add(id);
                else if (entry is DesktopMenu menu) Collect(menu.Items, ids);
            }
        }
    }

    internal bool ShowsMenuItem(string id) => Volatile.Read(ref _menuItems).Contains(id);

    // dialogs/1: one dialog at a time, owned by the native window; the services that show them, created on first use.
    private int _dialog;
    private HostDialogServices? _dialogs;
    private readonly object _dialogGate = new();

    internal bool TryBeginDialog() => Interlocked.CompareExchange(ref _dialog, 1, 0) == 0;

    // menus/1: the open context menu's replacement signal; a new menu cancels the open one's (host-protocol.md, Context menus).
    private CancellationTokenSource? _contextMenu;

    internal void ReplaceContextMenu(CancellationTokenSource next)
    {
        if (Interlocked.Exchange(ref _contextMenu, next) is not { } open) return;
        try { open.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void EndContextMenu(CancellationTokenSource menu) => Interlocked.CompareExchange(ref _contextMenu, null, menu);

    internal void EndDialog() => Volatile.Write(ref _dialog, 0);

    // window-services/1: whether the host watches its monitors for the window's monitors-changed; the first caller starts.
    private int _monitorWatch;

    internal bool BeginMonitorWatch() => Interlocked.Exchange(ref _monitorWatch, 1) == 0;

    // The monitors the guest listed last, or null before its first list.
    private StrongBox<ImmutableArray<DesktopMonitor>>? _listedMonitors;

    internal ImmutableArray<DesktopMonitor>? ListedMonitors
    {
        get => Volatile.Read(ref _listedMonitors)?.Value;
        set => Volatile.Write(ref _listedMonitors, value is { } list ? new(list) : null);
    }

    // The window's dialog services; once the window is closing, services that report the closed owner, so a request
    // that raced the close creates none that nothing would dispose.
    // A region window's services are its host window's while a region shows it (host-protocol.md, Features in a region),
    // created again for another host window, and report the unavailable owner while it is detached.
    internal HostDialogServices Dialogs(Func<DesktopWindow, HostDialogServices> create)
    {
        HostDialogServices? replaced = null;
        HostDialogServices services;
        lock (_dialogGate)
        {
            if (_closing.IsCancellationRequested)
                return new HostDialogServices(DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.OwnerClosed), null, PlatformUnavailableReason.OwnerClosed);
            if (NativeWindow is not { } native)
                return new HostDialogServices(DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.OwnerUnavailable), null, PlatformUnavailableReason.OwnerUnavailable);
            if (_dialogs is not null && !ReferenceEquals(_dialogsFor, native))
            {
                replaced = _dialogs;
                _dialogs = null;
            }
            if (_dialogs is null)
            {
                _dialogs = create(native);
                _dialogsFor = native;
            }
            services = _dialogs;
        }
        if (replaced is not null) _ = replaced.DisposeAsync().AsTask();
        return services;
    }

    private DesktopWindow? _dialogsFor;

    // Cancelled when the native window closes, which ends its dialogs and context menus (host-protocol.md, Dialogs).
    private readonly CancellationTokenSource _closing = new();

    internal CancellationToken Closing => _closing.Token;

    internal async Task EndUserRequestsAsync()
    {
        try { await _closing.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        HostDialogServices? dialogs;
        lock (_dialogGate) (dialogs, _dialogs) = (_dialogs, null);
        if (dialogs is not null) await dialogs.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the guest to close the window, as the user's close does: a Window that guards its close decides first.
    /// </summary>
    /// <param name="force">Whether the window closes whatever the guest decides, such as when the host application quits.</param>
    /// <param name="cancellationToken">Cancels the wait for the guest's decision; the decision may still close the window.</param>
    /// <returns>
    /// Whether the guest allowed the close; the window then closes once the guest's Window has, and <see cref="Closed"/>
    /// completes. <see langword="true"/> for a window that is already closed or whose guest has gone.
    /// </returns>
    public async ValueTask<bool> RequestCloseAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!IsOpen || _guestClosed.Task.IsCompleted) return true;
        var allow = await Application.CloseRequestAsync(this, force, cancellationToken).ConfigureAwait(false);
        // A guest that allows but does not close its Window within the stop timeout loses the window anyway.
        if (allow) _ = CloseIfGuestDoesNotAsync();
        return allow;
    }

    internal void Attach(DesktopWindow presentation, DesktopWindowPresentation adapter, bool userStarted = false)
    {
        _presentation = presentation;
        Adapter = adapter;
        Features = adapter.Features;
        // The window opened at the guest's request (window-open): when it has the focus now, the guest gave it, and when
        // the platform deferred the activation, its next activation is the guest's. A guest's first window that the host
        // marked as started by the user (RunicGuestStartInfo.UserStarted) is the user's instead, until the guest asks to
        // activate it again.
        lock (_activationGate)
        {
            _active = presentation.Activity is { IsActive: true };
            _firstActivationIsUsers = userStarted && !_active;
            _guestActivated = _active && !userStarted;
            _guestActivationPending = !_active && !userStarted;
        }
        presentation.ActivityChanged += (_, _) => Observe();
    }

    // A window the policy embedded (region/1): it has no native window of its own; its region decides what it shows.
    internal void AttachRegion(RunicHostedRegion region, RunicWindowPresentationFeatures features)
    {
        Region = region;
        Features = features;
        lock (_activationGate)
        {
            _active = false;
            _guestActivated = false;
            _guestActivationPending = false;
        }
    }

    // A region window follows another host window (or none): its activation starts from that window's, which neither the
    // guest nor the user changed for it; an activation that follows is judged as for any window.
    internal void FollowHostWindow(DesktopWindow? native)
    {
        lock (_activationGate)
        {
            _active = native?.Activity is { IsActive: true };
            _guestActivated = false;
            _guestActivationPending = false;
            _firstActivationIsUsers = false;
        }
    }

    // The host's native window that shows the window: its own, or for a region window the host window that shows the
    // region now, null while it is detached.
    internal DesktopWindow? NativeWindow => Region is { } region ? region.NativeWindow : _presentation;

    // The native window an owned window of this one opens over: for a region window, the host window that shows the
    // region or showed it last.
    internal DesktopWindow? OwnerWindow => Region is { } region ? region.NativeWindow ?? region.LastNativeWindow : _presentation;

    // ----- Activation (host-protocol.md, Window services): whether the window is active because the guest asked.

    // How long after the guest's last request that can activate one of its windows an activation counts as the guest's
    // (RunicHostedApplication.NoteGuestActivation), and how long after the user's activation ended a clipboard write is
    // still allowed.
    internal static readonly TimeSpan GuestActivationWindow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ClipboardWriteGrace = TimeSpan.FromSeconds(1);

    private readonly object _activationGate = new();
    // When an activation that was not the guest's ended (a Stopwatch timestamp; 0 for none).
    private long _userActivationEnded;
    private bool _active;
    private bool _guestActivated;
    // The guest asked to activate the window, and the platform did not activate it right away, such as macOS while the
    // host application is not active, or a desktop's focus-stealing prevention: its next activation, however late, is the
    // guest's, and consumes this.
    private bool _guestActivationPending;
    // The window is the first of a guest the user started (RunicGuestStartInfo.UserStarted): its first activation is the
    // user's, unless the guest asked again meanwhile.
    private bool _firstActivationIsUsers;

    // The user worked in the window through the host's own UI: chose an item of its native menu bar, or of a context
    // menu shown while the user worked in the window, or dropped files onto it. Never for the page's input, a shortcut
    // or a dialog's answer.
    internal void NoteUserInteraction()
    {
        lock (_activationGate) _guestActivated = false;
    }

    // The guest asked to activate or show this window (present, set-visible true, set-full-screen, toggle-maximized),
    // and the request applied: when the window is not active now, the platform deferred the activation, and the next one
    // is the guest's.
    internal void NoteGuestActivationRequested()
    {
        lock (_activationGate)
        {
            _firstActivationIsUsers = false;
            if (!Observe().Active) _guestActivationPending = true;
        }
    }

    // The window's activation, read once for one decision; in a region, the host window's with the region's focus.
    internal ActivationSnapshot Activation => Region is { } region ? region.Combine(Observe()) : Observe();

    // The user works in the window: it is active, and not because of the guest's own request.
    internal bool IsUserActive => Activation is { Active: true, GuestActivated: false };

    // The platform's activity, read at once under the gate, so concurrent observations apply in order; its change event
    // follows on the thread pool, so a change the event has not reported yet counts from now.
    private ActivationSnapshot Observe()
    {
        lock (_activationGate)
        {
            var active = NativeWindow?.Activity is { IsActive: true };
            if (active && !_active)
            {
                // Any guest's request that can activate one of its windows, also another window's, such as the close of
                // an owned window that activates its owner, makes the activations that follow it a guest's. So does an
                // activation of this window that the guest asked for and the platform deferred, however late it comes.
                _guestActivated = _guestActivationPending || (!_firstActivationIsUsers && Application.IsGuestActivationRecent);
                _guestActivationPending = false;
                _firstActivationIsUsers = false;
            }
            else if (!active && _active)
            {
                _userActivationEnded = _guestActivated ? 0 : Stopwatch.GetTimestamp();
                _guestActivated = false;
            }
            _active = active;
            var inWriteGrace = !active && _userActivationEnded != 0 && Stopwatch.GetElapsedTime(_userActivationEnded) <= ClipboardWriteGrace;
            return new ActivationSnapshot(active, active && _guestActivated, inWriteGrace);
        }
    }

    // Whether the window is active, whether because of the guest's own request, and whether the user's activation
    // ended less than ClipboardWriteGrace ago.
    internal readonly record struct ActivationSnapshot(bool Active, bool GuestActivated, bool InWriteGrace)
    {
        internal bool UserActive => Active && !GuestActivated;
    }

    // The native window as IRunicWindowPresentation, for the requests it shares with a Window's presentation.
    internal DesktopWindowPresentation? Adapter { get; private set; }

    internal DesktopWindow? PresentationOrNull => _presentation;

    internal bool IsClosedByGuest => _guestClosed.Task.IsCompleted;

    // What the window can do, as its window-open reply listed it.
    internal RunicWindowPresentationFeatures Features { get; private set; }

    // The native window's close confirmation (DesktopWindowOptions.ConfirmCloseAsync): the guest decides, and the window
    // closes once its Window has closed (host-protocol.md, close-request), or when the guest is gone.
    internal async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken)
    {
        if (_guestClosed.Task.IsCompleted || !Application.IsChannelOpen) return true;
        if (!await Application.CloseRequestAsync(this, force: false, cancellationToken).ConfigureAwait(false)) return false;
        try { await _guestClosed.Task.WaitAsync(Application.StopTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return true;
    }

    private async Task CloseIfGuestDoesNotAsync()
    {
        try { await _guestClosed.Task.WaitAsync(Application.StopTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { await CloseNativeAsync().ConfigureAwait(false); }
    }

    // window-closed, or the guest is gone: the window closes without asking anyone.
    internal Task OnGuestClosedAsync()
    {
        _guestClosed.TrySetResult();
        return CloseNativeAsync();
    }

    internal async Task CloseNativeAsync()
    {
        // A region window has no native window: its region ends, and the window closes with it.
        if (Region is not null)
        {
            await Application.CloseRegionWindowAsync(this).ConfigureAwait(false);
            return;
        }
        if (_presentation is not { } window) return;
        try { await window.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    internal void MarkClosed() => _closed.TrySetResult();

    // The host's pushes for the window, one after the other, so they arrive in the order the window changed. A push of
    // a kind that is still waiting is replaced by the newer one in its place, so a guest that reads slowly, such as
    // during a long drag, receives the latest state instead of a queue of every change.
    internal void Push(string kind, Func<JsonObject> push)
    {
        lock (_pushGate)
        {
            if (_pendingPushes.FindIndex(pending => pending.Kind == kind) is var index and >= 0) _pendingPushes[index] = (kind, push);
            else _pendingPushes.Add((kind, push));
            if (_pushing) return;
            _pushing = true;
        }
        _ = Task.Run(SendPushesAsync);
    }

    // Completes when the pushes queued now have been sent, so a request that follows them, such as a region-attach after
    // a region's activity, reaches the guest after them.
    internal Task PushesSent()
    {
        lock (_pushGate)
        {
            if (!_pushing) return Task.CompletedTask;
            return (_pushesSent ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private TaskCompletionSource? _pushesSent;

    private async Task SendPushesAsync()
    {
        while (true)
        {
            Func<JsonObject> next;
            lock (_pushGate)
            {
                if (_pendingPushes.Count == 0)
                {
                    _pushing = false;
                    _pushesSent?.TrySetResult();
                    _pushesSent = null;
                    return;
                }
                next = _pendingPushes[0].Push;
                _pendingPushes.RemoveAt(0);
            }
            // A push that cannot be sent is dropped; the channel's own failure ends the guest.
            try { await Application.PushAsync(next()).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
}
