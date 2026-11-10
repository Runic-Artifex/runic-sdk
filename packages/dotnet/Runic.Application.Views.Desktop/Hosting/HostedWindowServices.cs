using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Menus;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Desktop.Hosting;

// menus/1 on the guest (host-protocol.md, Menus): a hosted Window's menu bar is the application's (DesktopHost.MenuBar),
// with each item's state from the Window's command path, as a native window of the guest would show it. The bar goes to
// the host with set-menu-bar when the window opened and whenever the application declares another; a changed state
// goes with update-menu-items. Requests go one at a time, so the host applies them in order, and changes made while one
// is sent are coalesced; a request the host's rate limit turned away is sent again a little later. The host's
// menu-invoke runs the item through the same command path. An item id names one item for the window's lifetime: an
// item of the new bar keeps its id when it was in the bar before, any other gets a new one, so a menu-invoke from a bar
// the host showed before the change runs the item the user chose, or nothing.
internal sealed class HostedWindowMenus : IDisposable
{
    private readonly HostedGuest _guest;
    private readonly string _window;
    private readonly DesktopHost _desktop;
    private readonly MenuCommandBinding? _commands;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private MenuCommandBinding? _application;
    private Dictionary<string, DesktopMenuItem> _items = new(StringComparer.Ordinal);
    private Dictionary<string, (bool Enabled, bool? Checked)> _states = new(StringComparer.Ordinal);
    // The ids of the current bar's items, by the item, and the last id given: ids are never given again.
    private Dictionary<DesktopMenuItem, string> _ids = new(ReferenceEqualityComparer.Instance);
    private long _lastId;
    private bool _sentBar;
    // The application's bar that the host has now; another one means the bar changed, also before MenuBarChanged ran.
    private IReadOnlyList<DesktopMenu>? _publishedBar;
    private bool _barChanged;
    private bool _statesChanged;
    private bool _sending;
    private bool _disposed;

    // commands: the Window's root ViewModel's binding, or null when it takes no part in the menu bar.
    internal HostedWindowMenus(HostedGuest guest, string window, DesktopHost desktop, MenuCommandBinding? commands, ILogger logger)
    {
        _guest = guest;
        _window = window;
        _desktop = desktop;
        _commands = commands;
        _logger = logger;
    }

    internal void Start()
    {
        _desktop.MenuBarChanged += OnMenuBarChanged;
        if (_commands is not null) _commands.Changed += OnStatesChanged;
        FollowApplicationCommands();
        lock (_gate) _barChanged = true;
        Send();
    }

    // DesktopHost.MenuCommands may change, such as through UseApplicationMenu; its states are followed while it is a binding.
    private void FollowApplicationCommands()
    {
        var current = _desktop.MenuCommands as MenuCommandBinding;
        MenuCommandBinding? previous;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(current, _application)) return;
            previous = _application;
            _application = current;
        }
        if (previous is not null) previous.Changed -= OnStatesChanged;
        if (current is not null) current.Changed += OnStatesChanged;
    }

    private void OnMenuBarChanged(object? sender, EventArgs eventArgs)
    {
        FollowApplicationCommands();
        lock (_gate) _barChanged = true;
        Send();
    }

    private void OnStatesChanged(object? sender, EventArgs eventArgs)
    {
        lock (_gate) _statesChanged = true;
        Send();
    }

    private void Send()
    {
        lock (_gate)
        {
            if (_disposed || _sending) return;
            _sending = true;
        }
        _ = Task.Run(SendAsync);
    }

    private async Task SendAsync()
    {
        while (true)
        {
            bool bar;
            lock (_gate)
            {
                if (_disposed || !(_barChanged || _statesChanged))
                {
                    _sending = false;
                    return;
                }
                // The bar changed when the application's bar is not the one the host has. A command binding can report a
                // change of the new bar before MenuBarChanged reaches this window: the new bar goes first, never states
                // of the old one, and goes once, whichever event woke the loop.
                bar = !ReferenceEquals(_desktop.MenuBar, _publishedBar);
                _barChanged = _statesChanged = false;
            }
            try
            {
                if (bar)
                {
                    if (BarRequest() is { } request && OverRate(await _guest.WindowRequestAsync(_window, request, CancellationToken.None).ConfigureAwait(false)))
                        await AgainAsync(bar: true).ConfigureAwait(false);
                }
                else if (UpdateRequest() is { } update)
                {
                    var (request, states, changed) = update;
                    if (OverRate(await _guest.WindowRequestAsync(_window, request, CancellationToken.None).ConfigureAwait(false)))
                    {
                        await AgainAsync(bar: false).ConfigureAwait(false);
                        continue;
                    }
                    // The host has the states now; a new bar meanwhile starts over with states of its own.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_states, states))
                        {
                            foreach (var (id, state) in changed) states[id] = state;
                        }
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedChannelFailed(_logger, error, error.GetType().Name); }
        }
    }

    // The host's rate limit turned the request away (host-protocol.md, Limits).
    private static bool OverRate(JsonObject? reply) =>
        reply is not null && HostProtocol.Boolean(reply, "ok") == false && reply["error"] is JsonObject error && HostProtocol.String(error, "reason") == "capacity";

    // Sends the bar or the states again after a pause, with whatever changed meanwhile.
    private async Task AgainAsync(bool bar)
    {
        await Task.Delay(RetryDelay).ConfigureAwait(false);
        lock (_gate)
        {
            // The host does not have the bar: it goes again.
            if (bar) _publishedBar = null;
            _statesChanged = true;
        }
    }

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private (bool Enabled, bool? Checked) State(DesktopMenuItem item)
    {
        var application = _desktop.MenuCommands;
        return (MenuCommandChain.CanExecute(item, _commands, application, _logger), MenuCommandChain.IsChecked(item, _commands, application, _logger));
    }

    // set-menu-bar with the application's bar now; null when there is none and the host shows none either.
    private JsonObject? BarRequest()
    {
        var menus = _desktop.MenuBar;
        var items = new Dictionary<string, DesktopMenuItem>(StringComparer.Ordinal);
        var states = new Dictionary<string, (bool Enabled, bool? Checked)>(StringComparer.Ordinal);
        var ids = new Dictionary<DesktopMenuItem, string>(ReferenceEqualityComparer.Instance);
        Dictionary<DesktopMenuItem, string> previous;
        lock (_gate) previous = _ids;
        // An item of the bar before keeps its id; any other item, and an item the bar shows twice, gets a new one.
        string NewId(DesktopMenuItem item)
        {
            var id = previous.TryGetValue(item, out var kept) && !items.ContainsKey(kept)
                ? kept
                : "i" + Interlocked.Increment(ref _lastId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ids.TryAdd(item, id);
            return id;
        }
        var entries = HostedWire.WriteEntries(menus, items, NewId, (id, item) => states[id] = State(item));
        lock (_gate)
        {
            _publishedBar = menus;
            _items = items;
            _states = states;
            _ids = ids;
            if (menus.Count == 0 && !_sentBar) return null;
            _sentBar = menus.Count > 0;
        }
        return new JsonObject { ["id"] = 0, ["kind"] = "set-menu-bar", ["menu"] = menus.Count == 0 ? null : entries };
    }

    // update-menu-items with the items whose state changed since the host last had them, and those states, which the
    // caller records once the host has them; null when none changed.
    private (JsonObject Request, Dictionary<string, (bool Enabled, bool? Checked)> States, List<(string Id, (bool Enabled, bool? Checked) State)> Changed)? UpdateRequest()
    {
        Dictionary<string, DesktopMenuItem> items;
        Dictionary<string, (bool Enabled, bool? Checked)> states;
        lock (_gate) (items, states) = (_items, _states);
        var changes = new JsonArray();
        var changed = new List<(string Id, (bool Enabled, bool? Checked) State)>();
        foreach (var (id, item) in items)
        {
            var state = State(item);
            bool same;
            lock (_gate) same = states.TryGetValue(id, out var previous) && previous == state;
            if (same) continue;
            changed.Add((id, state));
            changes.Add((JsonNode)new JsonObject { ["id"] = id, ["enabled"] = state.Enabled, ["checked"] = state.Checked });
        }
        return changes.Count == 0 ? null : (new JsonObject { ["id"] = 0, ["kind"] = "update-menu-items", ["items"] = changes }, states, changed);
    }

    // menu-invoke: the item the user chose in the host's bar, or whose shortcut they pressed; an id of no current item is
    // ignored. Runs off the channel's receive loop.
    internal void Invoke(string itemId)
    {
        DesktopMenuItem? item;
        lock (_gate) item = _disposed ? null : _items.GetValueOrDefault(itemId);
        if (item is null) return;
        _ = Task.Run(() => MenuCommandChain.TryExecute(item, _commands, _desktop.MenuCommands, _logger));
    }

    public void Dispose()
    {
        MenuCommandBinding? application;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            application = _application;
            _application = null;
        }
        _desktop.MenuBarChanged -= OnMenuBarChanged;
        if (_commands is not null)
        {
            _commands.Changed -= OnStatesChanged;
            _commands.Dispose();
        }
        if (application is not null) application.Changed -= OnStatesChanged;
    }
}

// The window services of a hosted Window (host-protocol.md, Menus and Dialogs): context menus with menus/1, file dialogs
// and message boxes with dialogs/1, each owned by the host's window; the other services, and those of a feature not in
// use, report OwnerUnavailable, as in serve-only mode. The Window's DesktopWindowPlatformServices binds to them when the
// window opened (DesktopBridgeWindow.SetHosted), and closing the window stops them: their requests end as OwnerClosed.
internal sealed class HostedWindowPlatformServices : IWindowPlatformServices, IAsyncDisposable
{
    private static readonly IWindowPlatformServices NoOwner = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private readonly HostedWindow _window;
    private readonly bool _menus;
    private readonly bool _dialogs;
    private readonly PresentationLifetime _lifetime;
    private readonly PresentationFiles? _files;
    private readonly PresentationMessageBoxes? _messageBoxes;
    // One dialog at a time (host-protocol.md, Dialogs): a second one fails as busy without asking the host.
    private int _dialog;

    internal HostedWindowPlatformServices(HostedWindow window, bool menus, bool dialogs)
    {
        _window = window;
        _menus = menus;
        _dialogs = dialogs;
        var owner = new HostedWindowOwner(window);
        _lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
        if (dialogs)
        {
            _files = new PresentationFiles(_lifetime, new HostedFileDialogs(this, new NativePickerBackend(owner, new HostedFilePicker(this))));
            _messageBoxes = new PresentationMessageBoxes(_lifetime, new HostedMessageBoxes(this));
        }
    }

    public IFileDialogs FileDialogs => (IFileDialogs?)_files ?? NoOwner.FileDialogs;
    public ITextClipboard Clipboard => NoOwner.Clipboard;
    public IDesktopFileLauncher FileLauncher => NoOwner.FileLauncher;
    public IDesktopMonitors Monitors => NoOwner.Monitors;
    public IDesktopMessageBoxes MessageBoxes => (IDesktopMessageBoxes?)_messageBoxes ?? NoOwner.MessageBoxes;

    public CapabilitySnapshot GetSnapshot()
    {
        var statuses = NoOwner.GetSnapshot().Statuses;
        if (_files is not null) statuses = statuses.SetItems(_files.GetSnapshot().Statuses);
        if (_messageBoxes is not null) statuses = statuses.SetItems(_messageBoxes.GetSnapshot().Statuses);
        CapabilityStatus menus = !_menus
            ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            : _window.IsClosed
                ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)
                : new CapabilityStatus.Available();
        statuses = statuses.SetItem(PlatformCapabilityIds.ContextMenu, menus).SetItem(PlatformCapabilityIds.MenuBar, menus);
        return new CapabilitySnapshot(_lifetime.Generation, statuses);
    }

    public async ValueTask<PlatformResult<DesktopMenuItem?>> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options = null, CancellationToken cancellationToken = default)
    {
        // The shared argument checks, as every implementation makes them.
        _ = await NoOwner.ShowContextMenuAsync(items, options, cancellationToken).ConfigureAwait(false);
        if (!_menus) return new PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        if (_window.IsClosed) return new PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        var chosen = new Dictionary<string, DesktopMenuItem>(StringComparer.Ordinal);
        var request = new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "show-context-menu",
            ["menu"] = HostedWire.WriteEntries(items, chosen, static (_, item) => (item.IsEnabled, item.IsChecked)),
            ["point"] = options is { X: { } x, Y: { } y } ? new JsonObject { ["x"] = x, ["y"] = y } : null,
        };
        var reply = await _window.Guest.UserRequestAsync(_window.Id, request, cancellationToken, AwaitsReplies).ConfigureAwait(false);
        return Outcome(reply) switch
        {
            null => new PlatformResult<DesktopMenuItem?>.Success(
                HostProtocol.String(reply!, "itemId") is { } id ? chosen.GetValueOrDefault(id) : null),
            PlatformResult<PlatformUnit>.Unavailable(var reason) => new PlatformResult<DesktopMenuItem?>.Unavailable(reason),
            PlatformResult<PlatformUnit>.Failed(var code, var diagnostic) => new PlatformResult<DesktopMenuItem?>.Failed(code, diagnostic),
            _ => new PlatformResult<DesktopMenuItem?>.Failed(PlatformFailureCode.Unknown),
        };
    }

    // A reply of a request that waits for the user: null for success, else its outcome; a closed channel is OwnerClosed.
    internal static PlatformResult<PlatformUnit>? Outcome(JsonObject? reply) =>
        reply is null
            ? new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            : HostedWindowPresentation.OutcomeOf(reply) is PlatformResult<PlatformUnit>.Success ? null : HostedWindowPresentation.OutcomeOf(reply);

    // A dialog of this window, or ResourceBusy while another one is shown.
    internal async ValueTask<T> DialogAsync<T>(Func<ValueTask<T>> show, Func<T> busy)
    {
        if (Interlocked.CompareExchange(ref _dialog, 1, 0) != 0) return busy();
        try { return await show().ConfigureAwait(false); }
        finally { Volatile.Write(ref _dialog, 0); }
    }

    internal HostedWindow Window => _window;

    // Whether a request the application gave up waits for the host's reply: not once the services stop with the window.
    private volatile bool _stopping;

    internal bool AwaitsReplies() => !_stopping;

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        try { await _lifetime.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is OwnerClosedException or AggregateException) { }
    }
}

// The owner of a hosted Window's dialogs: available while the window is open. The host shows them, so nothing runs on a
// native thread of the guest.
internal sealed class HostedWindowOwner(HostedWindow window) : INativeWindowOwner
{
    public Guid Generation { get; } = Guid.NewGuid();

    public bool IsAvailable => !window.IsClosed && !window.Guest.IsHostGone;

    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new OwnerClosedException());
}

// The file dialogs of a hosted Window: those of the picker below, one at a time.
internal sealed class HostedFileDialogs(HostedWindowPlatformServices services, NativePickerBackend backend) : IPickerBackend
{
    public bool IsAvailable => backend.IsAvailable;
    public bool SupportsAtomicReplace => backend.SupportsAtomicReplace;
    public bool SupportsDirectorySelection => backend.SupportsDirectorySelection;
    public bool SupportsMultipleSelection => backend.SupportsMultipleSelection;
    public bool SupportsMultipleDirectorySelection => backend.SupportsMultipleDirectorySelection;
    public bool SupportsDocumentAccess => backend.SupportsDocumentAccess;

    private static PickerResult<T> Busy<T>() where T : class, IAsyncDisposable => new PickerResult<T>.Failed(PlatformFailureCode.ResourceBusy);

    public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.OpenDirectoryAsync(options, cancellationToken), Busy<IDirectoryLease>);
    public ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.OpenDirectoriesAsync(options, cancellationToken), Busy<IDirectoryLeaseList>);
    public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.OpenFileAsync(options, cancellationToken), Busy<IReadFileLease>);
    public ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.OpenFilesAsync(options, cancellationToken), Busy<IReadFileLeaseList>);
    public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.SaveFileAsync(options, cancellationToken), Busy<ISaveFileLease>);
    public ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.OpenDocumentAsync(options, cancellationToken), Busy<IDocumentFileLease>);
    public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
        services.DialogAsync(() => backend.SaveDocumentAsync(options, cancellationToken), Busy<IDocumentFileLease>);
}

// The picker of a hosted Window's file dialogs: file-dialog asks the host, which shows its own dialog owned by the hosted
// window, and the guest opens the paths it returns itself, through NativePickerBackend's leases, as a native picker's.
// A dismissed dialog is null; the host's outcomes become the exceptions NativePickerBackend reports as results.
internal sealed class HostedFilePicker(HostedWindowPlatformServices services) : INativeFilePicker, INativeDirectoryPicker
{
    public bool SupportsMultipleSelection => true;
    public bool SupportsMultipleDirectorySelection => true;

    public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        var reply = await AskAsync(HostedWire.WriteFileDialog(request, folder: false), cancellationToken).ConfigureAwait(false);
        if (reply is null) return null;
        var filter = HostProtocol.Integer(reply, "filter") is { } index && index >= 0 && index < request.Filters.Length
            ? request.Filters[(int)index]
            : null;
        return [.. Paths(reply).Select((path, position) => new NativeFileSelection(path, null, AllowsSiblingReplacement: true)
        {
            SelectedFilter = position == 0 ? filter : null,
        })];
    }

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        await SelectDirectoryAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        var reply = await AskAsync(HostedWire.WriteFileDialog(request with { AllowMultiple = false }, folder: true), cancellationToken).ConfigureAwait(false);
        return reply is null ? null : Paths(reply).Select(static path => new NativeDirectorySelection(path, null)).FirstOrDefault();
    }

    public async ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        await SelectDirectoriesAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        var reply = await AskAsync(HostedWire.WriteFileDialog(request with { AllowMultiple = true }, folder: true), cancellationToken).ConfigureAwait(false);
        return reply is null ? null : [.. Paths(reply).Select(static path => new NativeDirectorySelection(path, null))];
    }

    // The successful reply, or null for a dismissed dialog; another outcome throws what NativePickerBackend maps to it.
    private async Task<JsonObject?> AskAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var window = services.Window;
        var reply = await window.Guest.UserRequestAsync(window.Id, request, cancellationToken, services.AwaitsReplies).ConfigureAwait(false);
        switch (HostedWindowPlatformServices.Outcome(reply))
        {
            case null:
                return reply;
            case PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed }:
                return null;
            case PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed or PlatformUnavailableReason.OwnerUnavailable }:
                throw new OwnerClosedException();
            case PlatformResult<PlatformUnit>.Unavailable:
                throw new NativeBackendUnavailableException();
            case PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }:
                throw new UnauthorizedAccessException("The host application refused the dialog.");
            case PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }:
                throw new ArgumentException("The host application could not show the dialog.");
            default:
                throw new IOException("The host application's dialog failed.");
        }
    }

    // The absolute paths of a successful reply; a path that is not one makes the dialog's result invalid.
    private static IEnumerable<string> Paths(JsonObject reply) =>
        reply["paths"] is JsonArray { Count: > 0 } paths
            ? paths.Select(static node => node is JsonValue value && value.TryGetValue<string>(out var path) && Path.IsPathFullyQualified(path) && !path.Contains('\0')
                ? path
                : throw new ArgumentException("The host application returned a path that is not absolute."))
            : throw new ArgumentException("The host application returned no path.");
}

// The message boxes of a hosted Window: message-box asks the host, which shows the box owned by the hosted window.
internal sealed class HostedMessageBoxes(HostedWindowPlatformServices services) : IDesktopMessageBoxes
{
    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        // The shared rules, as every implementation checks them.
        _ = await DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.BackendUnavailable).ShowAsync(messageBox, cancellationToken).ConfigureAwait(false);
        return await services.DialogAsync(() => AskAsync(messageBox, cancellationToken),
            static () => new PlatformResult<DesktopMessageBoxButton?>.Failed(PlatformFailureCode.ResourceBusy)).ConfigureAwait(false);
    }

    private async ValueTask<PlatformResult<DesktopMessageBoxButton?>> AskAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken)
    {
        var window = services.Window;
        var reply = await window.Guest.UserRequestAsync(window.Id, HostedWire.WriteMessageBox(messageBox), cancellationToken, services.AwaitsReplies).ConfigureAwait(false);
        return HostedWindowPlatformServices.Outcome(reply) switch
        {
            // A button the box does not have reads as none, as a box closed without a button.
            null => new PlatformResult<DesktopMessageBoxButton?>.Success(HostProtocol.String(reply!, "button") is { } id
                ? messageBox.Buttons.IsDefault ? null : messageBox.Buttons.FirstOrDefault(button => button.Id == id)
                : null),
            PlatformResult<PlatformUnit>.Unavailable(var reason) => new PlatformResult<DesktopMessageBoxButton?>.Unavailable(reason),
            PlatformResult<PlatformUnit>.Failed(var code, var diagnostic) => new PlatformResult<DesktopMessageBoxButton?>.Failed(code, diagnostic),
            _ => new PlatformResult<DesktopMessageBoxButton?>.Failed(PlatformFailureCode.Unknown),
        };
    }
}
