using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// B10 in-page menu bar (D036.8, D036.10): the reserved Bridge routes through which a page renders DesktopHost.MenuBar
// with its own components (@runic-artifex/views createMenuBarController). The wire is specs/desktop/menu-bar-page.md.
// - __runicMenuBarState: the current state. __runicMenuBarWait({ after }): the next state after a revision, a long poll
//   bounded per connection. __runicMenuBarInvoke({ revision, key }): runs an item through
//   DesktopWindow.TryExecuteMenuItemAsync, the native bar's command path. __runicMenuBarControl({ shortcuts }): the
//   connection's "app" lease, under which the page matches the items' shortcuts itself and the host's own path runs
//   nothing; it ends when the connection closes.
// - These are reserved routes, not Views protocol messages: Views protocol 3 is unchanged, and a host without them
//   leaves the page's controller "unavailable".
// - The state follows the bar, the window's placement and the availability of the commands: MenuCommandBinding raises
//   Changed from CanExecuteChanged. A custom IDesktopMenuCommandTarget without it is asked when the state is built.
internal sealed class MenuBarPageRoutes : IDisposable
{
    internal const string StateRoute = "__runicMenuBarState";
    internal const string WaitRoute = "__runicMenuBarWait";
    internal const string InvokeRoute = "__runicMenuBarInvoke";
    internal const string ControlRoute = "__runicMenuBarControl";
    // The version of the state envelope; a later host may add members, never change these.
    internal const int Protocol = 1;
    internal const int MaximumWaitersPerConnection = 4;
    internal const int MaximumWaiters = 64;
    private const int MaximumRequestLength = 4096;
    private const int MaximumKeyLength = 256;

    private readonly object _gate = new();
    private readonly DesktopHost _host;
    private readonly DesktopMenuBarPresentation _presentation;
    private readonly MenuPlatform _platform;
    private readonly ILogger _logger;
    private readonly IDisposable[] _bindings;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private Snapshot _snapshot = Snapshot.Initial;
    private IReadOnlyList<DesktopMenu>? _menus;
    private DesktopWindow? _window;
    private MenuCommandBinding? _binding;
    private MenuCommandBinding? _application;
    private bool _suspended;
    private bool _rebuilding;
    private bool _dirty;
    private bool _disposed;

    internal MenuBarPageRoutes(DesktopHost host, IBridgeTransport transport, DesktopMenuBarPresentation presentation, ILogger logger,
        MenuPlatform? platform = null)
    {
        _host = host;
        _presentation = presentation;
        _platform = platform ?? DesktopMenuPlanner.CurrentPlatform;
        _logger = logger;
        var bindings = new List<IDisposable>();
        try
        {
            bindings.Add(transport.Bind(StateRoute, State));
            bindings.Add(transport.BindAsync(WaitRoute, WaitAsync));
            bindings.Add(transport.BindAsync(InvokeRoute, InvokeAsync));
            bindings.Add(transport.Bind(ControlRoute, Control));
            _bindings = [.. bindings];
        }
        catch
        {
            foreach (var binding in bindings) binding.Dispose();
            throw;
        }
        host.MenuBarChanged += OnChanged;
        Invalidate();
    }

    // The current revision, for tests.
    internal long Revision
    {
        get { lock (_gate) return _snapshot.Revision; }
    }

    // The window whose commands the page runs, and its root ViewModel's commands, once the presentation opened.
    internal void Attach(DesktopWindow window, MenuCommandBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (_disposed || _window is not null) return;
            _window = window;
            _binding = binding;
        }
        window.MenuBarPlacementChanged += OnChanged;
        if (binding is not null) binding.Changed += OnChanged;
        Invalidate();
    }

    // A closed connection, such as a reloaded page, gives up its shortcut lease.
    internal void ReleaseConnection(string connectionKey)
    {
        lock (_gate)
        {
            if (_disposed || !_leases.Remove(connectionKey)) return;
            Publish(_snapshot with { Revision = _snapshot.Revision + 1 });
        }
        UpdateSuspension();
    }

    public void Dispose()
    {
        DesktopWindow? window;
        MenuCommandBinding? binding, application;
        Waiter[] waiters;
        bool suspended;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            window = _window;
            binding = _binding;
            application = _application;
            waiters = [.. _waiters];
            _waiters.Clear();
            _leases.Clear();
            suspended = _suspended;
            _suspended = false;
        }
        _host.MenuBarChanged -= OnChanged;
        if (window is not null) window.MenuBarPlacementChanged -= OnChanged;
        if (binding is not null) binding.Changed -= OnChanged;
        if (application is not null) application.Changed -= OnChanged;
        _stop.Cancel();
        foreach (var waiter in waiters) waiter.Completion.TrySetResult();
        foreach (var route in _bindings) route.Dispose();
        if (suspended) window?.SuspendHostMenuShortcuts(false);
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
                    DesktopLog.MenuCommandFailed(_logger, error, "*", error.GetType().FullName ?? error.GetType().Name);
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
        DesktopWindow? window;
        lock (_gate)
        {
            if (_disposed) return;
            window = _window;
        }
        FollowApplicationCommands();
        var menus = _host.MenuBar;
        var plan = DesktopMenuPlanner.PlanBar(menus, _platform);
        var items = new List<PlannedMenuEntry>();
        CollectItems(plan, items);
        var open = window is { IsOpen: true };
        bool[] runnable;
        try
        {
            runnable = open && items.Count > 0
                ? await window!.CanExecuteMenuItemsAsync([.. items.Select(static entry => entry.Item!)], _stop.Token).ConfigureAwait(false)
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
        var placement = open ? PlacementOf(window!) : "none";
        var hostShortcuts = open && window!.Browser == BrowserKind.Embedded && window.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar);
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
        var application = _host.MenuCommands as MenuCommandBinding;
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

    // Where the page is told the bar is. A Page window whose host reports no placement, such as an installed browser or
    // a window host without a native bar, renders it in the page; macOS keeps the application bar (D036.13).
    private string PlacementOf(DesktopWindow window) => window.MenuBarPlacement switch
    {
        DesktopMenuBarPlacement.Window => "window",
        DesktopMenuBarPlacement.Global => "global",
        DesktopMenuBarPlacement.Page => "page",
        _ => _presentation == DesktopMenuBarPresentation.Page ? "page" : "none",
    };

    // Called under the gate: the new state, and every waiting poll answered.
    private void Publish(Snapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var waiter in _waiters) waiter.Completion.TrySetResult();
        _waiters.Clear();
    }

    // The host's own matching is suspended while a page that shows the bar holds the "app" lease.
    private void UpdateSuspension()
    {
        DesktopWindow? window;
        bool suspend;
        lock (_gate)
        {
            if (_disposed) return;
            window = _window;
            suspend = _snapshot.Placement == "page" && _leases.Count > 0;
            if (window is null || suspend == _suspended) return;
            _suspended = suspend;
        }
        window.SuspendHostMenuShortcuts(suspend);
    }

    private string State(IBridgeArguments arguments)
    {
        lock (_gate) return _disposed ? Released : Envelope(_snapshot, arguments.ConnectionKey);
    }

    private async ValueTask<string> WaitAsync(IBridgeArguments arguments, CancellationToken cancellationToken)
    {
        if (ReadRequest(arguments) is not { } request || !request.TryGetProperty("after", out var afterValue)
            || !afterValue.TryGetInt64(out var after))
            return InvalidRequest;
        var connection = arguments.ConnectionKey ?? "";
        Waiter waiter;
        lock (_gate)
        {
            if (_disposed) return Released;
            if (_snapshot.Revision != after) return Envelope(_snapshot, connection);
            if (_waiters.Count >= MaximumWaiters
                || _waiters.Count(waiting => waiting.Connection == connection) >= MaximumWaitersPerConnection)
                return Busy;
            waiter = new Waiter(connection);
            _waiters.Add(waiter);
        }
        try
        {
            await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _waiters.Remove(waiter);
        }
        lock (_gate) return _disposed ? Released : Envelope(_snapshot, connection);
    }

    private async ValueTask<string> InvokeAsync(IBridgeArguments arguments, CancellationToken cancellationToken)
    {
        if (ReadRequest(arguments) is not { } request
            || !request.TryGetProperty("revision", out var revisionValue) || !revisionValue.TryGetInt64(out var revision)
            || !request.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String
            || keyValue.GetString() is not { Length: > 0 and <= MaximumKeyLength } key)
            return InvalidRequest;
        Snapshot snapshot;
        DesktopWindow? window;
        lock (_gate)
        {
            if (_disposed) return Released;
            snapshot = _snapshot;
            window = _window;
        }
        // The page chose from the bar it rendered: an item of an older bar may now be another command.
        if (revision < snapshot.MenusRevision || revision > snapshot.Revision || !snapshot.Items.TryGetValue(key, out var item))
            return Result("stale");
        if (window is null) return Result("disabled");
        try
        {
            return Result(await window.TryExecuteMenuItemAsync(item, cancellationToken).ConfigureAwait(false) ? "executed" : "disabled");
        }
        catch (ObjectDisposedException)
        {
            return Result("disabled");
        }
    }

    private string Control(IBridgeArguments arguments)
    {
        if (ReadRequest(arguments) is not { } request || !request.TryGetProperty("shortcuts", out var value)
            || value.ValueKind != JsonValueKind.String || value.GetString() is not ("app" or "runic") || arguments.ConnectionKey is not { } connection)
            return InvalidRequest;
        var app = value.GetString() == "app";
        string reply;
        lock (_gate)
        {
            if (_disposed) return Released;
            _leases.TryGetValue(connection, out var count);
            var next = app ? count + 1 : Math.Max(0, count - 1);
            if (next == 0) _leases.Remove(connection);
            else _leases[connection] = next;
            if ((count == 0) != (next == 0)) Publish(_snapshot with { Revision = _snapshot.Revision + 1 });
            reply = Envelope(_snapshot, connection);
        }
        UpdateSuspension();
        return reply;
    }

    private static JsonElement? ReadRequest(IBridgeArguments arguments)
    {
        string text;
        try
        {
            text = arguments.GetString();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
        if (text.Length > MaximumRequestLength) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Called under the gate.
    private string Envelope(Snapshot snapshot, string? connection)
    {
        var shortcuts = snapshot.Placement == "page" && connection is not null && _leases.ContainsKey(connection) ? "app"
            : snapshot.HostShortcuts ? "host"
            : "page";
        return MenuBarPageState.WriteEnvelope(snapshot.Revision, snapshot.Placement, shortcuts, _platform, snapshot.MenusJson);
    }

    private static string Result(string kind) => "{\"kind\":\"" + kind + "\"}";

    private const string Released = "{\"kind\":\"released\"}";
    private const string Busy = "{\"kind\":\"busy\"}";
    private const string InvalidRequest = "{\"kind\":\"invalid-request\"}";

    private sealed record Snapshot(long Revision, long MenusRevision, string Placement, bool HostShortcuts, string MenusJson,
        ImmutableDictionary<string, DesktopMenuItem> Items)
    {
        internal static Snapshot Initial { get; } =
            new(0, 0, "none", false, "[]", ImmutableDictionary.Create<string, DesktopMenuItem>(StringComparer.Ordinal));
    }

    private sealed class Waiter(string connection)
    {
        internal string Connection { get; } = connection;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    internal static string WriteEnvelope(long revision, string placement, string shortcuts, MenuPlatform platform, string menusJson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "state");
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
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
