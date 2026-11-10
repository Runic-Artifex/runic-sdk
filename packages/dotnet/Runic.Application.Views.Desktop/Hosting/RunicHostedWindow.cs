using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// A native window of the host that shows one Window of a guest application (the Runic host protocol,
/// <c>runic-host/1</c>, in window mode).
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
    public DesktopWindow Presentation => _presentation ?? throw new InvalidOperationException("The window has not opened yet.");

    /// <summary>Whether the window is open.</summary>
    public bool IsOpen => !_closed.Task.IsCompleted && _presentation is { IsOpen: true };

    /// <summary>Completes when the window has closed, for any reason.</summary>
    public Task Closed => _closed.Task;

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

    internal void Attach(DesktopWindow presentation, DesktopWindowPresentation adapter)
    {
        _presentation = presentation;
        Adapter = adapter;
        Features = adapter.Features;
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
