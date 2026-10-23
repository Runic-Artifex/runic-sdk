using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.DBus.Menus;

namespace Runic.Desktop.Internal;

// What the Linux global menu needs from its GTK 3 or GTK 4 window. Members other than InvokeAsync and Report run on the
// window's GTK thread.
internal interface ILinuxGlobalMenuWindow
{
    // Runs the action on the window's GTK thread. Faults or does nothing once the window has closed; the menu cancels
    // the token when it stops.
    Task InvokeAsync(Action action, CancellationToken cancellationToken);

    // Whether the window's command path can run the item now (the window's target, then the application's).
    bool CanExecute(DesktopMenuItem item);

    // Runs the item through the window's command path, which asks CanExecute first.
    void Execute(DesktopMenuItem item);

    // The bar went into the desktop's global menu (true) or came back into the window (false).
    void PlacementChanged(bool global);

    // Sets KDE's application menu properties on the window's X11 window, as Qt does: the menu's bus name and path.
    void Annotate(string serviceName, string objectPath);

    // A diagnostic of the window (any thread): global-menu-failed or global-menu-permission.
    void Report(DesktopDiagnostic diagnostic);
}

// B10, D036.9: one X11 window's menu bar in the desktop's global menu, such as KDE Plasma's Global Menu applet or the
// title bar's application menu button.
// - The bar is exported as com.canonical.dbusmenu (DBusMenuServer) at /org/runicartifex/MenuBar/<n> on the process's
//   shared session connection, with the planner's entries: ids by planned key (a changed toggle kind is a new id, as
//   Plasma's importer requires), _ before the access key, shortcuts as aas and check or radio marks.
// - The window is associated with the menu as Qt does: com.canonical.AppMenu.Registrar.RegisterWindow(xid, path) from
//   the exporting connection, and the _KDE_NET_WM_APPMENU_* properties on the X11 window.
// - The registrar's owner decides the placement: kded owns it only while a global menu view exists. The owner is
//   watched, so the bar moves when one appears or goes away, and the window registers again with a new owner.
// - enabled is the declaration and CanExecute, read on the GTK thread when the layout is built and again when a client
//   is about to show a submenu. A click runs the item on the GTK thread through the window's command path.
// Wayland (org_kde_kwin_appmenu) is not handled here: a Wayland window keeps its bar in the window.
internal sealed class LinuxGlobalMenu : IDBusMenuOwner, IAsyncDisposable
{
    internal const string RegistrarName = "com.canonical.AppMenu.Registrar";
    internal const string RegistrarInterface = "com.canonical.AppMenu.Registrar";
    internal const string RegistrarPath = "/com/canonical/AppMenu/Registrar";
    internal const string PathPrefix = "/org/runicartifex/MenuBar/";
    // The window diagnostics of the global menu, which Runic Desktop logs as events 3016 and 3015.
    internal const string FailedCode = "global-menu-failed";
    internal const string PermissionCode = "global-menu-permission";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Lock BusGate = new();
    private static Task<DBusConnection?>? s_bus;
    private static int s_windows;
    private static int s_permissionReported;

    private readonly ILinuxGlobalMenuWindow _window;
    private readonly uint _xid;
    private readonly DBusMenuServer _server;
    private readonly MenuIdAllocator _ids = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly Lock _lock = new();
    private DBusMenuLayout _layout = DBusMenuLayout.Empty;
    private uint _revision;
    private Task _run = Task.CompletedTask;
    private DBusConnection? _connection;
    private IAsyncDisposable? _export;
    private string? _path;
    private volatile bool _registered;
    private int _failureReported;
    private int _disposed;
    // GTK thread: the placement last passed to the window.
    private bool _global;

    private LinuxGlobalMenu(ILinuxGlobalMenuWindow window, uint xid)
    {
        _window = window;
        _xid = xid;
        _server = new DBusMenuServer(this);
    }

    // The menu's object path once exported; for tests.
    internal string? ObjectPath => Volatile.Read(ref _path);

    // The connection the menu is exported on; for tests.
    internal DBusConnection? Connection => Volatile.Read(ref _connection);

    // GTK thread. Exports the window's planned bar and follows the registrar from now on.
    internal static LinuxGlobalMenu Start(ILinuxGlobalMenuWindow window, uint xid, ImmutableArray<PlannedMenuEntry> plan)
    {
        ArgumentNullException.ThrowIfNull(window);
        var menu = new LinuxGlobalMenu(window, xid);
        menu.SetMenus(plan);
        menu._run = Task.Run(menu.RunAsync);
        return menu;
    }

    // GTK thread. Replaces the exported bar and tells clients to read it again.
    internal void SetMenus(ImmutableArray<PlannedMenuEntry> plan)
    {
        var nodes = new Dictionary<int, DBusMenuNode>();
        var children = ImmutableArray.CreateBuilder<int>(plan.Length);
        foreach (var menu in plan) children.Add(Add(nodes, menu));
        nodes[0] = new DBusMenuNode(0, DBusMenuNode.Describe(submenu: true), children.ToImmutable());
        uint revision;
        lock (_lock)
        {
            revision = ++_revision;
            _layout = new DBusMenuLayout(revision, nodes);
        }
        if (Volatile.Read(ref _export) is not null) _server.LayoutUpdated(revision);
    }

    private int Add(Dictionary<int, DBusMenuNode> nodes, PlannedMenuEntry entry)
    {
        var id = _ids[entry.Key];
        ImmutableArray<int> children = entry.Kind == PlannedMenuKind.Menu ? [.. entry.Children.Select(child => Add(nodes, child))] : [];
        nodes[id] = new DBusMenuNode(id, Describe(entry, Available(entry)), children, entry);
        return id;
    }

    private bool Available(PlannedMenuEntry entry) =>
        entry.Enabled && (entry.Item is not { } item || _window.CanExecute(item));

    // The dbusmenu properties of a planned entry with its availability.
    internal static ImmutableArray<KeyValuePair<string, DBusValue>> Describe(PlannedMenuEntry entry, bool enabled) => entry.Kind switch
    {
        PlannedMenuKind.Separator => DBusMenuNode.Describe(separator: true),
        PlannedMenuKind.Menu => DBusMenuNode.Describe(label: entry.MnemonicLabel, enabled: enabled, submenu: true),
        _ => DBusMenuNode.Describe(label: entry.MnemonicLabel, enabled: enabled,
            toggleType: entry.Kind switch { PlannedMenuKind.Check => "checkmark", PlannedMenuKind.Radio => "radio", _ => null },
            toggled: entry.Item?.IsChecked == true, shortcut: entry.Shortcut?.DBusMenu ?? default),
    };

    public DBusMenuLayout Layout
    {
        get
        {
            lock (_lock) return _layout;
        }
    }

    public void Clicked(DBusMenuNode node)
    {
        if (_stopped.IsCancellationRequested || node.Source is not PlannedMenuEntry { Enabled: true, Item: { } item }) return;
        _ = ExecuteAsync(item);
    }

    private async Task ExecuteAsync(DesktopMenuItem item)
    {
        try
        {
            await _window.InvokeAsync(() =>
            {
                if (!_stopped.IsCancellationRequested) _window.Execute(item);
            }, _stopped.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The window closed before the click reached it.
        }
    }

    // As AppKit validates items and the in-window bars ask when a menu opens: the entries of the submenu with their
    // CanExecute now. Changed entries are sent with ItemsPropertiesUpdated, and the caller reads the layout again.
    public async ValueTask<bool> AboutToShowAsync(DBusMenuNode node)
    {
        if (_stopped.IsCancellationRequested) return false;
        DBusMenuLayout layout;
        lock (_lock) layout = _layout;
        var changed = new List<DBusMenuNode>();
        try
        {
            await _window.InvokeAsync(() =>
            {
                foreach (var id in node.Children)
                {
                    if (!layout.Nodes.TryGetValue(id, out var child) || child.Source is not PlannedMenuEntry { Item: not null } entry) continue;
                    var enabled = Available(entry);
                    if ((child["enabled"] is null) != enabled) changed.Add(child with { Properties = Describe(entry, enabled) });
                }
            }, _stopped.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        if (changed.Count == 0) return false;
        lock (_lock)
        {
            // A layout replaced meanwhile already has the new state.
            if (!ReferenceEquals(layout, _layout)) return true;
            var nodes = layout.Nodes.ToDictionary();
            foreach (var update in changed) nodes[update.Id] = update;
            _layout = new DBusMenuLayout(layout.Revision, nodes);
        }
        _server.ItemsPropertiesUpdated(changed);
        return true;
    }

    private async Task RunAsync()
    {
        var token = _stopped.Token;
        // No session bus: the bar stays in the window, without a diagnostic.
        if (await SharedBusAsync().ConfigureAwait(false) is not { } connection || token.IsCancellationRequested) return;
        var path = PathPrefix + Interlocked.Increment(ref s_windows).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            lock (_lock)
            {
                if (token.IsCancellationRequested) return;
                _export = connection.Export(new DBusObjectPath(path), _server);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            ReportFailure($"The menu could not be exported at {path} ({error.GetType().Name}).");
            return;
        }
        Volatile.Write(ref _connection, connection);
        Volatile.Write(ref _path, path);
        // Clients read the layout from the revision they were told about.
        _server.LayoutUpdated(Layout.Revision);
        try
        {
            await _window.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) _window.Annotate(connection.UniqueName, path);
            }, _stopped.Token).ConfigureAwait(false);
            await foreach (var owner in connection.WatchNameOwnerAsync(RegistrarName, token).ConfigureAwait(false))
            {
                if (owner is null)
                {
                    _registered = false;
                    ReportPermission();
                    await PlaceAsync(false).ConfigureAwait(false);
                    continue;
                }
                // A registrar that starts or restarts knows no window yet.
                var registered = await RegisterAsync(connection, path, token).ConfigureAwait(false);
                await PlaceAsync(registered).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is DBusException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection or the window went away.
        }
        // The session bus went away: the window shows its own bar again.
        _registered = false;
        if (!token.IsCancellationRequested) await PlaceAsync(false).ConfigureAwait(false);
    }

    private async Task<bool> RegisterAsync(DBusConnection connection, string path, CancellationToken token)
    {
        try
        {
            await connection.CallMethodAsync(new DBusMethodCall(RegistrarName, new DBusObjectPath(RegistrarPath), RegistrarInterface, "RegisterWindow")
            {
                Arguments = [DBusValue.From(_xid), DBusValue.From(new DBusObjectPath(path))],
                Timeout = Timeout,
            }, token).ConfigureAwait(false);
            _registered = true;
            return true;
        }
        catch (DBusException error)
        {
            _registered = false;
            ReportFailure($"The global menu registrar refused the window: {error.ErrorName}.");
            return false;
        }
    }

    private async Task PlaceAsync(bool global)
    {
        try
        {
            await _window.InvokeAsync(() =>
            {
                if (_global == global || (global && _stopped.IsCancellationRequested)) return;
                _global = global;
                _window.PlacementChanged(global);
            }, _stopped.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The window closed.
        }
    }

    // Once per window: the bar stays in the window.
    private void ReportFailure(string message)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        _window.Report(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, FailedCode, message, Retryable: true,
            Remediation: "The menu bar stays in the window. Check the desktop's global menu service (kded's appmenu module on KDE Plasma).")
        {
            Severity = DesktopDiagnosticSeverity.Warning,
        });
    }

    // Once per process: in a Flatpak sandbox on KDE Plasma, the registrar looks unowned without the permission to talk to it.
    private void ReportPermission()
    {
        if (!IsFlatpakOnPlasma(File.Exists("/.flatpak-info"), Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"))
            || Interlocked.Exchange(ref s_permissionReported, 1) != 0)
            return;
        _window.Report(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, PermissionCode,
            "The Flatpak sandbox shows no global menu registrar, so the menu bar stays in the window.", Retryable: true,
            Remediation: "Add --talk-name=com.canonical.AppMenu.Registrar to the Flatpak finish arguments, or ignore this when no global menu is shown.")
        {
            Severity = DesktopDiagnosticSeverity.Warning,
        });
    }

    internal static bool IsFlatpakOnPlasma(bool flatpak, string? currentDesktop) =>
        flatpak && currentDesktop is { } desktops && desktops.Split(':').Any(static desktop => desktop.Equals("KDE", StringComparison.OrdinalIgnoreCase));

    // The process's session connection for every window's menu. One attempt serves every caller; a failed or closed
    // connection is replaced on the next request.
    private static Task<DBusConnection?> SharedBusAsync()
    {
        lock (BusGate)
        {
            if (s_bus is not { } current || current.IsFaulted || current.IsCanceled
                || current is { IsCompletedSuccessfully: true, Result: null or { Disconnected.IsCancellationRequested: true } })
                s_bus = current = ConnectAsync();
            return current;
        }
    }

    private static async Task<DBusConnection?> ConnectAsync()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            return await DBusConnection.ConnectAsync(DBusBus.Session, deadline.Token).ConfigureAwait(false) is PlatformResult<DBusConnection>.Success(var connection)
                ? connection
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IAsyncDisposable? export;
        lock (_lock)
        {
            _stopped.Cancel();
            export = _export;
            _export = null;
        }
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Stopping reports nothing.
        }
        if (_registered && Volatile.Read(ref _connection) is { Disconnected.IsCancellationRequested: false } connection)
        {
            _registered = false;
            try
            {
                await connection.CallMethodAsync(new DBusMethodCall(RegistrarName, new DBusObjectPath(RegistrarPath), RegistrarInterface, "UnregisterWindow")
                {
                    Arguments = [DBusValue.From(_xid)],
                    Timeout = Timeout,
                }).ConfigureAwait(false);
            }
            catch (DBusException)
            {
                // The registrar also forgets a window that is destroyed.
            }
        }
        if (export is not null) await export.DisposeAsync().ConfigureAwait(false);
    }

    // KDE's application menu properties of an X11 window: STRING, format 8, as Qt's xcb plugin writes them.
    internal static class X11
    {
        internal const string ServiceNameProperty = "_KDE_NET_WM_APPMENU_SERVICE_NAME";
        internal const string ObjectPathProperty = "_KDE_NET_WM_APPMENU_OBJECT_PATH";
        private const nuint XaString = 31;
        private const int PropModeReplace = 0;
        private static readonly Lazy<nint> Library = new(static () =>
            OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libX11.so.6", out var library) ? library : 0);

        // GTK thread, with GDK's X display and the toplevel's X window.
        internal static unsafe bool Annotate(nint display, nuint window, string serviceName, string objectPath)
        {
            if (display == 0 || window == 0 || Library.Value is var x11 && x11 == 0) return false;
            if (!NativeLibrary.TryGetExport(x11, "XInternAtom", out var internAtom)
                || !NativeLibrary.TryGetExport(x11, "XChangeProperty", out var changeProperty)
                || !NativeLibrary.TryGetExport(x11, "XFlush", out var flush))
                return false;
            Set(display, window, ServiceNameProperty, serviceName);
            Set(display, window, ObjectPathProperty, objectPath);
            ((delegate* unmanaged[Cdecl]<nint, int>)flush)(display);
            return true;

            void Set(nint display, nuint window, string name, string value)
            {
                var atomName = Marshal.StringToCoTaskMemUTF8(name);
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                try
                {
                    var atom = ((delegate* unmanaged[Cdecl]<nint, nint, int, nuint>)internAtom)(display, atomName, 0);
                    fixed (byte* data = bytes)
                    {
                        ((delegate* unmanaged[Cdecl]<nint, nuint, nuint, nuint, int, int, byte*, int, int>)changeProperty)(
                            display, window, atom, XaString, 8, PropModeReplace, data, bytes.Length);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(atomName);
                }
            }
        }
    }
}
