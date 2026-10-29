using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Menus;

// B10 in-page menu bar (D036.8, D036.10, D036.14): the reserved route through which a page renders the host's menu bar
// with its own components (@runic-artifex/views createMenuBarController). The wire is menu bar page protocol 2 in
// specs/desktop/menu-bar-page.md, on the Views protocol 4 channel. One source for both hosts:
// Runic.Application.Views.CsWebUi compiles this file, with MenuCommandBinding.cs and the planner, through its own
// IMenuBarSource and IMenuBarPageWindow; it references neither Runic.Desktop nor Views.Desktop.
// - Requests to the reserved route runic.menu-bar, after the connection's hello: menu-bar-watch answers the state and
//   pushes each later state to the connection; menu-bar-unwatch ends that; menu-bar-invoke runs an item through
//   IMenuBarPageWindow.TryExecuteAsync, the native bar's command path on Runic Desktop; menu-bar-shortcuts takes or
//   returns the connection's "app" lease, under which the page matches the items' shortcuts itself and the host's own
//   path runs nothing. A closed connection ends its watch and its leases.
// - The pushes are targeted, because `shortcuts` differs per connection. On CS-WebUI they travel through the Views
//   mailbox. Each push carries the whole state, so a connection receives the latest state, in revision order.
// - The state follows the bar, the window's placement and the availability of the commands: MenuCommandBinding raises
//   Changed from CanExecuteChanged. A custom IDesktopMenuCommandTarget without it is asked when the state is built.

// The bar a host declares and its application-wide commands: DesktopHost on Runic Desktop, RunicCsWebUiHost's bar on
// CS-WebUI. MenuBarChanged may be raised on any thread.
internal interface IMenuBarSource
{
    IReadOnlyList<DesktopMenu> MenuBar { get; }
    event EventHandler? MenuBarChanged;
    // The application's commands, which raise Changed as a window's do; null for none or for another target.
    MenuCommandBinding? ApplicationCommands { get; }
}

// The window whose page renders the bar.
internal interface IMenuBarPageWindow
{
    bool IsOpen { get; }
    // Where the page is told the bar is while the window is open: "page", "window", "global" or "none".
    string Placement { get; }
    // Whether the host matches the items' shortcuts on its own key path.
    bool HostShortcuts { get; }
    event EventHandler? PlacementChanged;
    // Whether each item can run now, asked as TryExecuteAsync asks. ObjectDisposedException once the window closed.
    Task<bool[]> CanExecuteAsync(IReadOnlyList<DesktopMenuItem> items, CancellationToken cancellationToken);
    // Runs the item as the native bar does; false when it is disabled or no command can run it now.
    ValueTask<bool> TryExecuteAsync(DesktopMenuItem item, CancellationToken cancellationToken);
    // While the page holds the "app" lease, the host's own matching is suspended.
    void SuspendHostShortcuts(bool suspended);
}

internal sealed class MenuBarPageRoutes : IDisposable, IBridgeRouteHandler
{
    // The reserved route of menu bar page protocol 2.
    internal const string Route = "runic.menu-bar";
    internal const string PushKind = "menu-bar";
    // The version of the state; a later host may add members, never change these.
    internal const int Protocol = 2;
    private const int MaximumKeyLength = 256;

    private readonly object _gate = new();
    private readonly IMenuBarSource _source;
    private readonly MenuPlatform _platform;
    private readonly ILogger _logger;
    private readonly BridgeDispatcher _dispatcher;
    private readonly IDisposable _route;
    private readonly IDisposable _connections;
    private readonly CancellationTokenSource _stop = new();
    // The connections that watch the state, and the revision each one was last sent.
    private readonly Dictionary<string, long> _watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private Snapshot _snapshot = Snapshot.Initial;
    private IReadOnlyList<DesktopMenu>? _menus;
    private IMenuBarPageWindow? _window;
    private MenuCommandBinding? _binding;
    private MenuCommandBinding? _application;
    private bool _suspended;
    private bool _rebuilding;
    private bool _dirty;
    private bool _delivering;
    private bool _undelivered;
    private bool _disposed;

    internal MenuBarPageRoutes(IMenuBarSource source, IBridgeTransport transport, ILogger logger, MenuPlatform? platform = null)
    {
        _source = source;
        _platform = platform ?? DesktopMenuPlanner.CurrentPlatform;
        _logger = logger;
        _dispatcher = BridgeDispatcher.For(transport);
        _route = _dispatcher.RegisterRoute(Route, this);
        try { _connections = _dispatcher.OnConnectionClosed(ReleaseConnection); }
        catch
        {
            _route.Dispose();
            throw;
        }
        source.MenuBarChanged += OnChanged;
        Invalidate();
    }

    // The current revision, for tests.
    internal long Revision
    {
        get { lock (_gate) return _snapshot.Revision; }
    }

    // The window whose commands the page runs, and its root ViewModel's commands, once the presentation opened.
    internal void Attach(IMenuBarPageWindow window, MenuCommandBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (_disposed || _window is not null) return;
            _window = window;
            _binding = binding;
        }
        window.PlacementChanged += OnChanged;
        if (binding is not null) binding.Changed += OnChanged;
        Invalidate();
    }

    // A closed connection, such as a reloaded page, ends its watch and gives up its shortcut leases.
    internal void ReleaseConnection(string connectionKey)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _watchers.Remove(connectionKey);
            if (!_leases.Remove(connectionKey)) return;
            Publish(_snapshot with { Revision = _snapshot.Revision + 1 });
        }
        UpdateSuspension();
    }

    public void Dispose()
    {
        IMenuBarPageWindow? window;
        MenuCommandBinding? binding, application;
        bool suspended;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            window = _window;
            binding = _binding;
            application = _application;
            _watchers.Clear();
            _leases.Clear();
            suspended = _suspended;
            _suspended = false;
        }
        _source.MenuBarChanged -= OnChanged;
        if (window is not null) window.PlacementChanged -= OnChanged;
        if (binding is not null) binding.Changed -= OnChanged;
        if (application is not null) application.Changed -= OnChanged;
        _stop.Cancel();
        _route.Dispose();
        _connections.Dispose();
        if (suspended) window?.SuspendHostShortcuts(false);
    }

    private void OnChanged(object? sender, EventArgs eventArgs) => Invalidate();

    // Builds the state again; changes that arrive while it builds lead to one more build.
    private void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_rebuilding)
            {
                _dirty = true;
                return;
            }
            _rebuilding = true;
        }
        _ = RebuildLoopAsync();
    }

    private async Task RebuildLoopAsync()
    {
        while (true)
        {
            lock (_gate) _dirty = false;
            try
            {
                await RebuildAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!_stop.IsCancellationRequested)
                    MenuBarLog.CommandFailed(_logger, error, "*");
            }
            lock (_gate)
            {
                if (!_dirty || _disposed)
                {
                    _rebuilding = false;
                    return;
                }
            }
        }
    }

    private async Task RebuildAsync()
    {
        IMenuBarPageWindow? window;
        lock (_gate)
        {
            if (_disposed) return;
            window = _window;
        }
        FollowApplicationCommands();
        var menus = _source.MenuBar;
        var plan = DesktopMenuPlanner.PlanBar(menus, _platform);
        var items = new List<PlannedMenuEntry>();
        CollectItems(plan, items);
        var open = window is { IsOpen: true };
        bool[] runnable;
        try
        {
            runnable = open && items.Count > 0
                ? await window!.CanExecuteAsync([.. items.Select(static entry => entry.Item!)], _stop.Token).ConfigureAwait(false)
                : new bool[items.Count];
        }
        catch (ObjectDisposedException)
        {
            open = false;
            runnable = new bool[items.Count];
        }
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, DesktopMenuItem>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            byKey[items[index].Key] = items[index].Item!;
            if (runnable[index]) enabled.Add(items[index].Key);
        }
        var menusJson = MenuBarPageState.WriteMenus(plan, entry => entry.Enabled && enabled.Contains(entry.Key));
        var placement = open ? window!.Placement : "none";
        var hostShortcuts = open && window!.HostShortcuts;
        lock (_gate)
        {
            if (_disposed) return;
            var current = _snapshot;
            var menusChanged = !ReferenceEquals(menus, _menus);
            _menus = menus;
            if (!menusChanged && current.MenusJson == menusJson && current.Placement == placement && current.HostShortcuts == hostShortcuts)
                return;
            var revision = current.Revision + 1;
            Publish(new Snapshot(revision, menusChanged ? revision : current.MenusRevision, placement, hostShortcuts, menusJson,
                byKey.ToImmutableDictionary(StringComparer.Ordinal)));
        }
        UpdateSuspension();
    }

    // UseApplicationMenu's commands raise Changed as a window's do; another target is asked when the state is built.
    private void FollowApplicationCommands()
    {
        var application = _source.ApplicationCommands;
        MenuCommandBinding? previous;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(application, _application)) return;
            previous = _application;
            _application = application;
        }
        if (previous is not null) previous.Changed -= OnChanged;
        if (application is not null) application.Changed += OnChanged;
    }

    private static void CollectItems(ImmutableArray<PlannedMenuEntry> entries, List<PlannedMenuEntry> items)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind == PlannedMenuKind.Menu) CollectItems(entry.Children, items);
            else if (entry.Item is not null) items.Add(entry);
        }
    }

    // Called under the gate: the new state, pushed to every watching connection.
    private void Publish(Snapshot snapshot)
    {
        _snapshot = snapshot;
        if (_watchers.Count == 0) return;
        _undelivered = true;
        if (_delivering) return;
        _delivering = true;
        _ = DeliverAsync();
    }

    // Pushes the latest state to each watching connection that was not sent it yet, one push at a time, so a
    // connection receives its states in revision order. A state that changes meanwhile is sent once more.
    private async Task DeliverAsync()
    {
        // Publish starts this under the gate; the pushes are built after the caller released it.
        await Task.Yield();
        while (true)
        {
            List<(string Connection, ReadOnlyMemory<byte> Push)> pushes = [];
            lock (_gate)
            {
                if (_disposed || !_undelivered)
                {
                    _delivering = false;
                    return;
                }
                _undelivered = false;
                foreach (var (connection, sent) in _watchers.ToArray())
                {
                    if (sent >= _snapshot.Revision) continue;
                    _watchers[connection] = _snapshot.Revision;
                    pushes.Add((connection, Encoding.UTF8.GetBytes(Push(_snapshot, connection))));
                }
            }
            foreach (var (connection, push) in pushes)
            {
                try { await _dispatcher.SendAsync(connection, push, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    MenuBarLog.CommandFailed(_logger, error, "*");
                }
            }
        }
    }

    // The host's own matching is suspended while a page that shows the bar holds the "app" lease.
    private void UpdateSuspension()
    {
        IMenuBarPageWindow? window;
        bool suspend;
        lock (_gate)
        {
            if (_disposed) return;
            window = _window;
            suspend = _snapshot.Placement == "page" && _leases.Count > 0;
            if (window is null || suspend == _suspended) return;
            _suspended = suspend;
        }
        window.SuspendHostShortcuts(suspend);
    }

    // Menu bar page protocol 2: the requests of the reserved route. Failures use the Views reply envelope.
    ValueTask<ReadOnlyMemory<byte>> IBridgeRouteHandler.HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        var root = request.Root;
        var connection = request.Connection.Key;
        switch (request.Kind)
        {
            case "menu-bar-watch":
                lock (_gate)
                {
                    if (_disposed) return new(BridgeMessages.Released);
                    _watchers[connection] = _snapshot.Revision;
                    return new(StateReply(_snapshot, connection));
                }
            case "menu-bar-unwatch":
                lock (_gate)
                {
                    if (_disposed) return new(BridgeMessages.Released);
                    _watchers.Remove(connection);
                }
                return new(BridgeMessages.Ok);
            case "menu-bar-invoke":
                if (!root.TryGetProperty("revision", out var revisionValue) || !BridgeRequestEnvelope.TryReadInteger(revisionValue, out var revision)
                    || revision < 0
                    || !root.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String
                    || keyValue.GetString() is not { Length: > 0 and <= MaximumKeyLength } key)
                    return new(InvalidRequest("A menu-bar-invoke request has a revision and a key."));
                return InvokeAsync(revision, key, cancellationToken);
            case "menu-bar-shortcuts":
                if (!root.TryGetProperty("shortcuts", out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not ("app" or "runic"))
                    return new(InvalidRequest("A menu-bar-shortcuts request has shortcuts \"app\" or \"runic\"."));
                return new(Shortcuts(connection, value.GetString() == "app"));
            default:
                return new(InvalidRequest($"'{request.Kind}' is not a request of the menu bar route."));
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> InvokeAsync(long revision, string key, CancellationToken cancellationToken)
    {
        Snapshot snapshot;
        IMenuBarPageWindow? window;
        lock (_gate)
        {
            if (_disposed) return BridgeMessages.Released;
            snapshot = _snapshot;
            window = _window;
        }
        // The page chose from the bar it rendered: an item of an older bar may now be another command.
        if (revision < snapshot.MenusRevision || revision > snapshot.Revision || !snapshot.Items.TryGetValue(key, out var item))
            return Result("stale");
        if (window is null) return Result("disabled");
        try
        {
            return Result(await window.TryExecuteAsync(item, cancellationToken).ConfigureAwait(false) ? "executed" : "disabled");
        }
        catch (ObjectDisposedException)
        {
            return Result("disabled");
        }
    }

    private byte[] Shortcuts(string connection, bool app)
    {
        byte[] reply;
        lock (_gate)
        {
            if (_disposed) return BridgeMessages.Released.ToArray();
            _leases.TryGetValue(connection, out var count);
            var next = app ? count + 1 : Math.Max(0, count - 1);
            if (next == 0) _leases.Remove(connection);
            else _leases[connection] = next;
            if ((count == 0) != (next == 0))
            {
                Publish(_snapshot with { Revision = _snapshot.Revision + 1 });
                // The reply carries this state, so the connection's own watch need not push it.
                if (_watchers.ContainsKey(connection)) _watchers[connection] = _snapshot.Revision;
            }
            reply = StateReply(_snapshot, connection);
        }
        UpdateSuspension();
        return reply;
    }

    // Called under the gate. Who matches the shortcuts for this connection.
    private string ShortcutsOf(Snapshot snapshot, string connection) =>
        snapshot.Placement == "page" && _leases.ContainsKey(connection) ? "app"
            : snapshot.HostShortcuts ? "host"
            : "page";

    // Called under the gate.
    private byte[] StateReply(Snapshot snapshot, string connection) =>
        MenuBarPageState.WriteReply(snapshot.Revision, snapshot.Placement, ShortcutsOf(snapshot, connection), _platform, snapshot.MenusJson);

    // Called under the gate.
    private string Push(Snapshot snapshot, string connection) =>
        MenuBarPageState.WritePush(snapshot.Revision, snapshot.Placement, ShortcutsOf(snapshot, connection), _platform, snapshot.MenusJson);

    private static byte[] Result(string result) => Encoding.UTF8.GetBytes("{\"ok\":true,\"result\":\"" + result + "\"}");

    private static byte[] InvalidRequest(string message) => BridgeMessages.Rejected("invalid-request", message);

    private sealed record Snapshot(long Revision, long MenusRevision, string Placement, bool HostShortcuts, string MenusJson,
        ImmutableDictionary<string, DesktopMenuItem> Items)
    {
        internal static Snapshot Initial { get; } =
            new(0, 0, "none", false, "[]", ImmutableDictionary.Create<string, DesktopMenuItem>(StringComparer.Ordinal));
    }

}

// The JSON of the in-page menu bar state (specs/desktop/menu-bar-page.md), from a planned bar.
internal static class MenuBarPageState
{
    internal static string WriteMenus(ImmutableArray<PlannedMenuEntry> plan, Func<PlannedMenuEntry, bool> enabled)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteEntries(writer, plan, enabled);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // {"ok":true,"state":{…}}: the reply to menu-bar-watch and menu-bar-shortcuts.
    internal static byte[] WriteReply(long revision, string placement, string shortcuts, MenuPlatform platform, string menusJson) =>
        BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("state");
            WriteState(writer, revision, placement, shortcuts, platform, menusJson);
            writer.WriteEndObject();
        });

    // {"kind":"menu-bar","route":"runic.menu-bar","state":{…}}: the push of a watched state.
    internal static string WritePush(long revision, string placement, string shortcuts, MenuPlatform platform, string menusJson) =>
        Encoding.UTF8.GetString(BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", MenuBarPageRoutes.PushKind);
            writer.WriteString("route", MenuBarPageRoutes.Route);
            writer.WritePropertyName("state");
            WriteState(writer, revision, placement, shortcuts, platform, menusJson);
            writer.WriteEndObject();
        }));

    private static void WriteState(Utf8JsonWriter writer, long revision, string placement, string shortcuts, MenuPlatform platform,
        string menusJson)
    {
        writer.WriteStartObject();
        writer.WriteNumber("protocol", MenuBarPageRoutes.Protocol);
        writer.WriteNumber("revision", revision);
        writer.WriteString("placement", placement);
        writer.WriteString("shortcuts", shortcuts);
        writer.WriteString("platform", platform switch
        {
            MenuPlatform.MacOS => "macos",
            MenuPlatform.Windows => "windows",
            _ => "linux",
        });
        writer.WritePropertyName("menus");
        writer.WriteRawValue(menusJson, skipInputValidation: true);
        writer.WriteEndObject();
    }

    private static void WriteEntries(Utf8JsonWriter writer, ImmutableArray<PlannedMenuEntry> entries, Func<PlannedMenuEntry, bool> enabled)
    {
        writer.WriteStartArray();
        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            switch (entry.Kind)
            {
                case PlannedMenuKind.Separator:
                    writer.WriteString("kind", "separator");
                    writer.WriteString("key", entry.Key);
                    break;
                case PlannedMenuKind.Menu:
                    var menu = (DesktopMenu)entry.Source;
                    writer.WriteString("kind", "menu");
                    WriteLabel(writer, entry, menu.AccessKey);
                    writer.WriteString("role", menu.Role switch
                    {
                        DesktopMenuRole.Application => "application",
                        DesktopMenuRole.File => "file",
                        DesktopMenuRole.Help => "help",
                        _ => "none",
                    });
                    writer.WriteBoolean("enabled", entry.Enabled);
                    writer.WritePropertyName("entries");
                    WriteEntries(writer, entry.Children, enabled);
                    break;
                default:
                    var item = entry.Item!;
                    writer.WriteString("kind", "item");
                    WriteLabel(writer, entry, item.AccessKey);
                    writer.WriteString("commandId", item.CommandId);
                    if (item.CommandParameter is { } parameter) writer.WriteString("commandParameter", parameter);
                    writer.WriteBoolean("enabled", enabled(entry));
                    if (item.IsChecked is { } isChecked) writer.WriteBoolean("checked", isChecked);
                    if (item.RadioGroup is { } group) writer.WriteString("radioGroup", group);
                    if (entry.Shortcut is { } shortcut)
                    {
                        writer.WriteString("shortcut", shortcut.Shortcut.ToString());
                        writer.WriteString("shortcutText", shortcut.DisplayText);
                        writer.WriteString("ariaKeyShortcuts", shortcut.AriaKeyShortcuts);
                    }
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteLabel(Utf8JsonWriter writer, PlannedMenuEntry entry, char? accessKey)
    {
        writer.WriteString("key", entry.Key);
        writer.WriteString("label", entry.Title);
        if (entry.AccessKeyIndex >= 0 && accessKey is { } key) writer.WriteString("accessKey", char.ToUpperInvariant(key).ToString());
        writer.WriteNumber("accessKeyIndex", entry.AccessKeyIndex);
    }
}
