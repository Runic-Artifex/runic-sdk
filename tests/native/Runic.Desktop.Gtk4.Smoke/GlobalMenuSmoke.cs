using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.DBus;

// --global-menu gtk3|gtk4 (B10 PR 7, D036.9) under Xvfb with GDK_BACKEND=x11 and a private session bus
// (dbus-run-session): the GTK 3 and GTK 4 windows' bar in the desktop's global menu, with a fake
// com.canonical.AppMenu.Registrar in this process.
// - The window registers its X11 window and menu path from the connection that exports the menu, and sets
//   _KDE_NET_WM_APPMENU_SERVICE_NAME and _KDE_NET_WM_APPMENU_OBJECT_PATH (STRING, format 8) on it, as Qt does.
// - Placement Global with MenuBarPlacementChanged, the in-window bar hidden; a dbusmenu click runs the window's command.
// - The registrar going away brings the bar back into the window; a new owner registers the window again.
// - A Page window exports nothing; closing the window unregisters it.
[SupportedOSPlatform("linux")]
internal static partial class GlobalMenuSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string RegistrarName = "com.canonical.AppMenu.Registrar";
    private const string RegistrarPath = "/com/canonical/AppMenu/Registrar";

    private sealed class Target(params string[] handled) : IDesktopMenuCommandTarget
    {
        private readonly HashSet<string> _handled = [.. handled];
        internal ConcurrentQueue<(string Id, string? Parameter)> Executed { get; } = new();
        public bool CanExecute(DesktopMenuItem item) => _handled.Contains(item.CommandId);
        public void Execute(DesktopMenuItem item) => Executed.Enqueue((item.CommandId, item.CommandParameter));
    }

    internal static int Run(string toolkit)
    {
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        options = options with { WaitForConnection = true, MenuBar = MenuSmoke.Bar };
        return DesktopEventLoop.Run(options, async host =>
        {
            try
            {
                return await RunAsync(host, toolkit);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Global menu failed: " + error.Message);
                Console.Error.WriteLine(error);
                throw;
            }
        });
    }

    private static async Task<int> RunAsync(DesktopHost host, string toolkit)
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is null)
            throw new InvalidOperationException("Global menu: run under dbus-run-session, never on the desktop's session bus.");
        using var x11 = Xlib.Open() ?? throw new InvalidOperationException("Global menu: no X display; run under xvfb-run with GDK_BACKEND=x11.");
        var registrar = await FakeRegistrar.StartAsync();
        var target = new Target("file.save", "view.zoom");
        const string Page = "<!doctype html><html><head><script src=\"webui.js\"></script></head><body>global menu</body></html>";
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 320 });
            window.MenuCommands = target;
            var placements = new ConcurrentQueue<DesktopMenuBarPlacement>();
            window.MenuBarPlacementChanged += (_, _) => placements.Enqueue(window.MenuBarPlacement);

            var (sender, xid, path) = await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            var expectedXid = await ReadNativeAsync(window, handle => toolkit == "gtk3" ? Gtk3.Xid(handle) : Gtk4.Xid(handle));
            Require(xid == expectedXid && xid != 0, $"RegisterWindow with the window's X11 id {expectedXid:x}, not {xid:x}");
            Require(path.StartsWith("/org/runicartifex/MenuBar/", StringComparison.Ordinal), $"the menu path, not {path}");
            Require(x11.ReadString(xid, "_KDE_NET_WM_APPMENU_SERVICE_NAME") == sender,
                $"_KDE_NET_WM_APPMENU_SERVICE_NAME {sender}, not {x11.ReadString(xid, "_KDE_NET_WM_APPMENU_SERVICE_NAME")}");
            Require(x11.ReadString(xid, "_KDE_NET_WM_APPMENU_OBJECT_PATH") == path,
                $"_KDE_NET_WM_APPMENU_OBJECT_PATH {path}, not {x11.ReadString(xid, "_KDE_NET_WM_APPMENU_OBJECT_PATH")}");
            Console.WriteLine($"PASS {toolkit} registration: RegisterWindow({xid:x}, {path}) from {sender} and both _KDE_NET_WM_APPMENU_* STRING properties.");

            // The window may have registered while it opened, before the handler was added.
            await WaitForAsync(() => window.MenuBarPlacement == DesktopMenuBarPlacement.Global, () => $"placement Global, not {window.MenuBarPlacement}");
            await WaitForAsync(async () => await ReadNativeAsync(window, handle => BarVisible(toolkit, handle)) == false, "the in-window bar to hide");

            // The global menu's view of the bar, and a click on Save.
            await using var client = await ConnectAsync();
            var layout = (await client.CallMethodAsync(new DBusMethodCall(sender, new DBusObjectPath(path), "com.canonical.dbusmenu", "GetLayout")
            {
                Arguments = [DBusValue.From(0), DBusValue.From(-1), DBusValue.StringArray([])],
            }))[1];
            var menus = layout.GetFields()[2].GetItems().Select(static menu => menu.GetVariant()).ToList();
            var labels = menus.Select(static menu => menu.GetFields()[1].GetVariantDictionary()["label"].GetString()).ToList();
            Require(labels.SequenceEqual(["_File", "_View", "Tools", "Help"]), "the exported menus: " + string.Join(", ", labels));
            var save = menus[0].GetFields()[2].GetItems()[0].GetVariant().GetFields();
            Require(save[1].GetVariantDictionary()["label"].GetString() == "_Save", "File's first item is _Save");
            await client.CallMethodAsync(new DBusMethodCall(sender, new DBusObjectPath(path), "com.canonical.dbusmenu", "Event")
            {
                Arguments = [save[0], DBusValue.From("clicked"), DBusValue.Variant(DBusValue.From(0)), DBusValue.From(0u)],
            });
            await WaitForAsync(() => target.Executed.TryDequeue(out var ran) && ran == ("file.save", null), "a dbusmenu click to run Save");
            Console.WriteLine($"PASS {toolkit} global placement: Global, the in-window bar hidden, the exported bar and a click running its command.");

            // The registrar goes away: the bar comes back into the window. A new owner registers the window again.
            await registrar.DisposeAsync();
            await WaitForAsync(() => window.MenuBarPlacement == DesktopMenuBarPlacement.Window, "placement Window after the registrar left");
            await WaitForAsync(async () => await ReadNativeAsync(window, handle => BarVisible(toolkit, handle)) == true, "the in-window bar to show");
            registrar = await FakeRegistrar.StartAsync();
            var again = await registrar.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Require(again == (sender, xid, path), $"the same registration again, not {again}");
            await WaitForAsync(() => window.MenuBarPlacement == DesktopMenuBarPlacement.Global, "placement Global with the new registrar");
            await WaitForAsync(() => placements.ToArray() is [.., DesktopMenuBarPlacement.Window, DesktopMenuBarPlacement.Global],
                () => "MenuBarPlacementChanged with Window, then Global: " + string.Join(", ", placements));
            Console.WriteLine($"PASS {toolkit} registrar watch: back in the window when it left, registered again with a new owner.");

            await window.CloseAsync();
            Require(await registrar.Unregistrations.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == xid, "UnregisterWindow when the window closes");
        }

        // A Page window has no native bar to export.
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 480, Height = 320, MenuBarPresentation = DesktopMenuBarPresentation.Page,
        }))
        {
            await Task.Delay(1000);
            Require(!registrar.Registrations.Reader.TryRead(out var stray), $"a Page window registered nothing, not {stray}");
            Require(window.MenuBarPlacement == DesktopMenuBarPlacement.Page, $"placement Page, not {window.MenuBarPlacement}");
        }
        await registrar.DisposeAsync();
        Console.WriteLine($"PASS {toolkit} global menu.");
        return 0;
    }

    private static bool BarVisible(string toolkit, nint window) => toolkit == "gtk3" ? Gtk3.BarVisible(window) : Gtk4.BarVisible(window);

    private static async Task<DBusConnection> ConnectAsync() =>
        await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection)
            ? connection
            : throw new InvalidOperationException("Global menu: no session bus.");

    private static async Task<T> ReadNativeAsync<T>(DesktopWindow window, Func<nint, T> read)
    {
        T value = default!;
        await window.InvokeNativeAsync(handle => value = read(handle));
        return value;
    }

    private static Task WaitForAsync(Func<bool> condition, string what) => WaitForAsync(() => Task.FromResult(condition()), what);

    private static Task WaitForAsync(Func<bool> condition, Func<string> what) => WaitForAsync(() => Task.FromResult(condition()), what);

    private static Task WaitForAsync(Func<Task<bool>> condition, string what) => WaitForAsync(condition, () => what);

    private static async Task WaitForAsync(Func<Task<bool>> condition, Func<string> what)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition().WaitAsync(Deadline))
        {
            if (elapsed.Elapsed > Deadline) throw new TimeoutException("Global menu: timed out waiting for " + what() + ".");
            await Task.Delay(50);
        }
    }

    private static void Require(bool condition, string what)
    {
        if (condition) return;
        Console.Error.WriteLine("Global menu: expected " + what + ".");
        throw new InvalidOperationException("Global menu: expected " + what + ".");
    }

    // com.canonical.AppMenu.Registrar as kded's appmenu module answers it, on a connection of its own.
    private sealed class FakeRegistrar : DBusObject, IAsyncDisposable
    {
        private DBusConnection? _connection;

        internal System.Threading.Channels.Channel<(string Sender, ulong Xid, string Path)> Registrations { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(string, ulong, string)>();

        internal System.Threading.Channels.Channel<ulong> Unregistrations { get; } = System.Threading.Channels.Channel.CreateUnbounded<ulong>();

        internal static async Task<FakeRegistrar> StartAsync()
        {
            var registrar = new FakeRegistrar { _connection = await ConnectAsync() };
            registrar._connection.Export(new DBusObjectPath(RegistrarPath), registrar);
            if (await registrar._connection.RequestNameAsync(RegistrarName, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("Global menu: the registrar name is taken on this bus.");
            return registrar;
        }

        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            if (context is { Member: "RegisterWindow", Signature.Value: "uo" })
                Registrations.Writer.TryWrite((context.Sender ?? "", context.Arguments[0].GetUInt32(), context.Arguments[1].GetObjectPath().Value));
            else if (context is { Member: "UnregisterWindow", Signature.Value: "u" })
                Unregistrations.Writer.TryWrite(context.Arguments[0].GetUInt32());
            else
                throw new DBusException(DBusException.UnknownMethod, context.Member);
            return ValueTask.FromResult(ImmutableArray<DBusValue>.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is { } connection) await connection.DisposeAsync();
            _connection = null;
        }
    }

    // The window's X11 id and its bar from the native handle, on the GTK thread.
    private static partial class Gtk3
    {
        private const string Library = "libgtk-3.so.0";

        internal static ulong Xid(nint window) => GdkX11WindowGetXid(GtkWidgetGetWindow(window));

        // The bar is the first child of the window's box; a hidden bar is not visible.
        internal static bool BarVisible(nint window)
        {
            var box = BinGetChild(window);
            var list = ContainerGetChildren(box);
            try
            {
                return list != 0 && Marshal.ReadIntPtr(list) is var first && first != 0 && WidgetGetVisible(first) != 0 && MenuBarIs(first);
            }
            finally
            {
                if (list != 0) ListFree(list);
            }
        }

        private static bool MenuBarIs(nint widget) => TypeCheckInstanceIsA(widget, MenuBarType()) != 0;

        [LibraryImport(Library, EntryPoint = "gtk_widget_get_window")] private static partial nint GtkWidgetGetWindow(nint widget);
        [LibraryImport(Library, EntryPoint = "gdk_x11_window_get_xid")] private static partial ulong GdkX11WindowGetXid(nint window);
        [LibraryImport(Library, EntryPoint = "gtk_bin_get_child")] private static partial nint BinGetChild(nint bin);
        [LibraryImport(Library, EntryPoint = "gtk_container_get_children")] private static partial nint ContainerGetChildren(nint container);
        [LibraryImport(Library, EntryPoint = "g_list_free")] private static partial void ListFree(nint list);
        [LibraryImport(Library, EntryPoint = "gtk_widget_get_visible")] private static partial int WidgetGetVisible(nint widget);
        [LibraryImport(Library, EntryPoint = "gtk_menu_bar_get_type")] private static partial nuint MenuBarType();
        [LibraryImport(Library, EntryPoint = "g_type_check_instance_is_a")] private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
    }

    private static partial class Gtk4
    {
        private static Gtk.Window Window(nint window) => (Gtk.Window)GObject.Internal.InstanceWrapper.WrapHandle<Gtk.Window>(window, false);

        internal static ulong Xid(nint window) => GdkX11SurfaceGetXid(Window(window).GetSurface()!.Handle.DangerousGetHandle());

        internal static bool BarVisible(nint window) =>
            Window(window).GetChild() is Gtk.Box box && box.GetFirstChild() is Gtk.PopoverMenuBar bar && bar.GetVisible();

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_x11_surface_get_xid")] private static partial ulong GdkX11SurfaceGetXid(nint surface);
    }

    // A connection of the smoke's own to the test display, for reading window properties.
    private sealed unsafe class Xlib : IDisposable
    {
        private readonly nint _library;
        private readonly nint _display;

        private Xlib(nint library, nint display) => (_library, _display) = (library, display);

        internal static Xlib? Open()
        {
            if (!NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBX11") ?? "libX11.so.6", out var library)
                && !NativeLibrary.TryLoad("libX11.so.6", out library))
                return null;
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(library, "XOpenDisplay"))(0);
            return display == 0 ? null : new Xlib(library, display);
        }

        // A STRING property in format 8, or null.
        internal string? ReadString(ulong window, string name)
        {
            var atomName = Marshal.StringToCoTaskMemUTF8(name);
            nuint atom;
            try
            {
                atom = ((delegate* unmanaged[Cdecl]<nint, nint, int, nuint>)NativeLibrary.GetExport(_library, "XInternAtom"))(_display, atomName, 1);
            }
            finally
            {
                Marshal.FreeCoTaskMem(atomName);
            }
            if (atom == 0) return null;
            nuint type, count, remaining;
            int format;
            byte* data = null;
            var getProperty = (delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int>)
                NativeLibrary.GetExport(_library, "XGetWindowProperty");
            if (getProperty(_display, (nuint)window, atom, 0, 1024, 0, 31, &type, &format, &count, &remaining, &data) != 0) return null;
            try
            {
                return data is null || type != 31 || format != 8 ? null : System.Text.Encoding.UTF8.GetString(data, (int)count);
            }
            finally
            {
                if (data is not null) _ = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "XFree"))((nint)data);
            }
        }

        public void Dispose() => _ = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "XCloseDisplay"))(_display);
    }
}
