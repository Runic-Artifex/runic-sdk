using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// B10, D036.9: one Wayland window's association with its global menu through KDE's org_kde_kwin_appmenu
// (plasma-wayland-protocols appmenu.xml, pinned in Internal/Wayland). KWin shows the dbusmenu at the address a client
// sets on a wl_surface. GTK has no API for it, so Runic speaks the protocol itself on GDK's wl_display:
// - org_kde_kwin_appmenu_manager is bound once per display on a private event queue: a wrapper of the display on that
//   queue, its own registry and one roundtrip, so GTK's queue never sees Runic's objects. Neither interface has events.
// - The wl_interface and wl_message descriptors that wayland-scanner would generate are built once in unmanaged memory
//   (AppMenuProtocol), with libwayland's own wl_surface_interface. GlobalMenuTests checks them against the pinned XML.
// - Requests go through wl_proxy_marshal_array_constructor(_versioned) and wl_proxy_marshal_array, which take an
//   argument array instead of C varargs.
// - KWin applies set_address at once, before or after the first commit, and keeps it with the surface. GTK 3 destroys
//   the window's wl_surface when it is hidden and creates another when it is shown, so its host detaches on unmap and
//   attaches the new surface on map. GTK 4 keeps one wl_surface for the window's lifetime, and KWin gives the address
//   to the toplevel it creates when the window is shown again.
// Every member runs on the GTK thread that owns the display.
internal sealed unsafe class WaylandAppMenu : IDisposable
{
    private const string ManagerName = "org_kde_kwin_appmenu_manager";
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, nint> Managers = [];
    private static Client? s_client;

    private readonly Client _client;
    private readonly nint _display;
    private readonly nint _manager;
    private (string Service, string Path)? _address;
    private nint _surface;
    private nint _appMenu;

    private WaylandAppMenu(Client client, nint display, nint manager) => (_client, _display, _manager) = (client, display, manager);

    // A window's appmenu on GDK's wl_display, or null when the compositor does not advertise org_kde_kwin_appmenu_manager
    // (GNOME, wlroots) or libwayland-client cannot be used. toolkitLibrary is the GTK library, which links libwayland-client.
    internal static WaylandAppMenu? Create(nint toolkitLibrary, nint display)
    {
        if (display == 0) return null;
        Client? client;
        nint manager;
        lock (Gate)
        {
            client = s_client ??= Client.Load(toolkitLibrary);
            if (client is null) return null;
            if (!Managers.TryGetValue(display, out manager))
            {
                manager = client.BindManager(display);
                Managers[display] = manager;
            }
        }
        return manager == 0 ? null : new WaylandAppMenu(client, display, manager);
    }

    // The menu's bus name and object path, set on the current surface and on every later one.
    internal void SetAddress(string serviceName, string objectPath, nint surface)
    {
        _address = (serviceName, objectPath);
        Detach();
        Attach(surface);
    }

    // Sets the address on the window's wl_surface, unless it is set there already. Zero leaves the window without one
    // until the next call.
    internal void Attach(nint surface)
    {
        if (surface == _surface || _address is not { } address) return;
        Detach();
        if (surface == 0) return;
        _appMenu = _client.CreateAppMenu(_manager, surface);
        if (_appMenu == 0) return;
        _surface = surface;
        _client.SetAddress(_appMenu, address.Service, address.Path);
        _client.Flush(_display);
    }

    // Releases the surface's appmenu object: the surface is gone or about to be.
    internal void Detach()
    {
        if (_appMenu != 0)
        {
            _client.Release(_appMenu);
            _client.Flush(_display);
        }
        _appMenu = 0;
        _surface = 0;
    }

    public void Dispose()
    {
        Detach();
        _address = null;
    }

    // libwayland-client's entry points, resolved through the GTK library that links it.
    private sealed class Client
    {
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _createQueue;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyQueue;
        private readonly delegate* unmanaged[Cdecl]<nint, nint> _createWrapper;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyWrapper;
        private readonly delegate* unmanaged[Cdecl]<nint, nint, void> _setQueue;
        private readonly delegate* unmanaged[Cdecl]<nint, nint*, nint, int> _addListener;
        private readonly delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, nint, nint> _marshalConstructor;
        private readonly delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, nint, uint, nint> _marshalConstructorVersioned;
        private readonly delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, void> _marshal;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyProxy;
        private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _roundtripQueue;
        private readonly delegate* unmanaged[Cdecl]<nint, int> _flush;
        private readonly nint _registryInterface;
        private readonly AppMenuProtocol _protocol;

        private Client(nint library)
        {
            _createQueue = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(library, "wl_display_create_queue");
            _destroyQueue = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "wl_event_queue_destroy");
            _createWrapper = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(library, "wl_proxy_create_wrapper");
            _destroyWrapper = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "wl_proxy_wrapper_destroy");
            _setQueue = (delegate* unmanaged[Cdecl]<nint, nint, void>)NativeLibrary.GetExport(library, "wl_proxy_set_queue");
            _addListener = (delegate* unmanaged[Cdecl]<nint, nint*, nint, int>)NativeLibrary.GetExport(library, "wl_proxy_add_listener");
            _marshalConstructor = (delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, nint, nint>)NativeLibrary.GetExport(library, "wl_proxy_marshal_array_constructor");
            _marshalConstructorVersioned = (delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, nint, uint, nint>)
                NativeLibrary.GetExport(library, "wl_proxy_marshal_array_constructor_versioned");
            _marshal = (delegate* unmanaged[Cdecl]<nint, uint, WlArgument*, void>)NativeLibrary.GetExport(library, "wl_proxy_marshal_array");
            _destroyProxy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "wl_proxy_destroy");
            _roundtripQueue = (delegate* unmanaged[Cdecl]<nint, nint, int>)NativeLibrary.GetExport(library, "wl_display_roundtrip_queue");
            _flush = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "wl_display_flush");
            _registryInterface = NativeLibrary.GetExport(library, "wl_registry_interface");
            _protocol = AppMenuProtocol.Build(NativeLibrary.GetExport(library, "wl_surface_interface"));
        }

        internal static Client? Load(nint library)
        {
            try
            {
                return library != 0 ? new Client(library) : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        // The manager proxy on a private queue of its own, or zero when the compositor does not advertise it.
        internal nint BindManager(nint display)
        {
            var queue = _createQueue(display);
            if (queue == 0) return 0;
            var wrapper = _createWrapper(display);
            if (wrapper == 0)
            {
                _destroyQueue(queue);
                return 0;
            }
            _setQueue(wrapper, queue);
            var getRegistry = stackalloc WlArgument[1];
            getRegistry[0] = default;
            // wl_display.get_registry (opcode 1): the registry inherits the wrapper's queue.
            var registry = _marshalConstructor(wrapper, 1, getRegistry, _registryInterface);
            _destroyWrapper(wrapper);
            if (registry == 0)
            {
                _destroyQueue(queue);
                return 0;
            }
            var global = new RegistryGlobal();
            nint manager = 0;
            if (_addListener(registry, RegistryListener.Pointer, (nint)(&global)) == 0 && _roundtripQueue(display, queue) >= 0 && global.Found != 0)
            {
                var version = Math.Min(global.Version, (uint)AppMenuProtocol.Version);
                using var name = new Utf8(ManagerName);
                var bind = stackalloc WlArgument[4];
                bind[0] = new WlArgument { Unsigned = global.Name };
                bind[1] = new WlArgument { Pointer = name.Pointer };
                bind[2] = new WlArgument { Unsigned = version };
                bind[3] = default;
                // wl_registry.bind (opcode 0, "usun").
                manager = _marshalConstructorVersioned(registry, 0, bind, (nint)_protocol.Manager, version);
            }
            _destroyProxy(registry);
            if (manager == 0) _destroyQueue(queue);
            // Otherwise the queue lives as long as the manager: for the process.
            return manager;
        }

        // org_kde_kwin_appmenu_manager.create (opcode 0, "no").
        internal nint CreateAppMenu(nint manager, nint surface)
        {
            var arguments = stackalloc WlArgument[2];
            arguments[0] = default;
            arguments[1] = new WlArgument { Pointer = surface };
            return _marshalConstructor(manager, 0, arguments, (nint)_protocol.AppMenu);
        }

        // org_kde_kwin_appmenu.set_address (opcode 0, "ss").
        internal void SetAddress(nint appMenu, string serviceName, string objectPath)
        {
            using var service = new Utf8(serviceName);
            using var path = new Utf8(objectPath);
            var arguments = stackalloc WlArgument[2];
            arguments[0] = new WlArgument { Pointer = service.Pointer };
            arguments[1] = new WlArgument { Pointer = path.Pointer };
            _marshal(appMenu, 0, arguments);
        }

        // org_kde_kwin_appmenu.release (opcode 1, a destructor).
        internal void Release(nint appMenu)
        {
            var none = stackalloc WlArgument[1];
            none[0] = default;
            _marshal(appMenu, 1, none);
            _destroyProxy(appMenu);
        }

        internal void Flush(nint display) => _ = _flush(display);
    }

    // What the registry listener found: the manager's global name and version.
    [StructLayout(LayoutKind.Sequential)]
    private struct RegistryGlobal
    {
        internal uint Name;
        internal uint Version;
        internal int Found;
    }

    // wl_registry_listener: global and global_remove. Lives for the process.
    private static class RegistryListener
    {
        internal static readonly nint* Pointer = Create();

        private static nint* Create()
        {
            var listener = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
            listener[0] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, uint, void>)&OnGlobal;
            listener[1] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, void>)&OnGlobalRemove;
            return listener;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnGlobal(nint data, nint registry, uint name, byte* interfaceName, uint version)
        {
            if (interfaceName is null || !MemoryMarshal.CreateReadOnlySpanFromNullTerminated(interfaceName).SequenceEqual("org_kde_kwin_appmenu_manager"u8)) return;
            var global = (RegistryGlobal*)data;
            global->Name = name;
            global->Version = version;
            global->Found = 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnGlobalRemove(nint data, nint registry, uint name)
        {
        }
    }

    // union wl_argument: the members Runic passes.
    [StructLayout(LayoutKind.Explicit)]
    private struct WlArgument
    {
        [FieldOffset(0)] internal uint Unsigned;
        [FieldOffset(0)] internal nint Pointer;
    }

    // A NUL-terminated UTF-8 copy for one call.
    private readonly struct Utf8(string value) : IDisposable
    {
        internal nint Pointer { get; } = Marshal.StringToCoTaskMemUTF8(value);

        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }
}

// struct wl_message and struct wl_interface of wayland-util.h.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlMessage
{
    internal byte* Name;
    internal byte* Signature;
    internal WlInterface** Types;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlInterface
{
    internal byte* Name;
    internal int Version;
    internal int MethodCount;
    internal WlMessage* Methods;
    internal int EventCount;
    internal WlMessage* Events;
}

// The descriptors wayland-scanner generates from appmenu.xml (version 2), in unmanaged memory for the process.
internal sealed unsafe class AppMenuProtocol
{
    internal const int Version = 2;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, AppMenuProtocol> Built = [];

    private AppMenuProtocol(WlInterface* manager, WlInterface* appMenu)
    {
        Manager = manager;
        AppMenu = appMenu;
    }

    internal WlInterface* Manager { get; }

    internal WlInterface* AppMenu { get; }

    // Once per wl_surface_interface: libwayland's, or a stand-in in tests.
    internal static AppMenuProtocol Build(nint surfaceInterface)
    {
        lock (Gate)
        {
            if (Built.TryGetValue(surfaceInterface, out var built)) return built;
            // types: two nulls for untyped arguments, then create's new_id and object.
            var types = (WlInterface**)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
            var appMenu = (WlInterface*)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));
            var manager = (WlInterface*)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));
            types[2] = appMenu;
            types[3] = (WlInterface*)surfaceInterface;
            *appMenu = Interface("org_kde_kwin_appmenu", [("set_address", "ss", (nint)types), ("release", "", (nint)types)]);
            *manager = Interface("org_kde_kwin_appmenu_manager", [("create", "no", (nint)(types + 2)), ("release", "2", (nint)types)]);
            built = new AppMenuProtocol(manager, appMenu);
            Built[surfaceInterface] = built;
            return built;
        }
    }

    private static WlInterface Interface(string name, (string Name, string Signature, nint Types)[] requests)
    {
        var methods = (WlMessage*)NativeMemory.AllocZeroed((nuint)requests.Length, (nuint)sizeof(WlMessage));
        for (var index = 0; index < requests.Length; index++)
            methods[index] = new WlMessage { Name = Text(requests[index].Name), Signature = Text(requests[index].Signature), Types = (WlInterface**)requests[index].Types };
        return new WlInterface { Name = Text(name), Version = Version, MethodCount = requests.Length, Methods = methods };
    }

    private static byte* Text(string value) => (byte*)Marshal.StringToCoTaskMemUTF8(value);

    // An interface as wayland-scanner describes it: name, version, requests (name, signature and the interface name of
    // each argument, null for untyped ones) and the number of events; for the tests.
    internal static (string Name, int Version, (string Name, string Signature, string?[] Types)[] Requests, int Events) Describe(WlInterface* value)
    {
        var requests = new (string, string, string?[])[value->MethodCount];
        for (var index = 0; index < requests.Length; index++)
        {
            var message = value->Methods[index];
            var signature = Read(message.Signature)!;
            var count = signature.Count(static code => char.IsLetter(code));
            var types = new string?[count];
            for (var argument = 0; argument < count; argument++)
                types[argument] = message.Types[argument] is var type && type is null ? null : Read(type->Name);
            requests[index] = (Read(message.Name)!, signature, types);
        }
        return (Read(value->Name)!, value->Version, requests, value->EventCount);
    }

    private static string? Read(byte* value) => value is null ? null : Marshal.PtrToStringUTF8((nint)value);
}
