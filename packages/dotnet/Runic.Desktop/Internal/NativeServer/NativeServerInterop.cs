using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal.NativeServer;

// The C ABI of native/runic-server (src/ffi.rs, include/runic_server.h), ABI version 1. Every struct mirrors its
// #[repr(C)] counterpart field by field.
internal static unsafe partial class NativeServerInterop
{
    internal const string Library = "runic_server";
    internal const uint AbiVersion = 1;

    internal const int Ok = 0;
    internal const int Pending = 1;
    internal const int InvalidArgument = -1;
    internal const int UnknownId = -2;
    internal const int Closed = -3;
    internal const int BindFailed = -4;

    internal const uint FlagAllInterfaces = 1;
    internal const uint RequestWebSocket = 1;

    internal const uint BodyEmpty = 0;
    internal const uint BodyStream = 1;
    internal const uint BodyFile = 2;

    internal const uint MessageText = 1;
    internal const uint MessageBinary = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunicStr
    {
        internal byte* Pointer;
        internal nuint Length;

        internal readonly ReadOnlySpan<byte> Span => Length == 0 ? default : new(Pointer, checked((int)Length));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunicHeader
    {
        internal RunicStr Name;
        internal RunicStr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunicRequest
    {
        internal ulong Id;
        internal uint Flags;
        internal ushort RemotePort;
        internal ushort LocalPort;
        internal RunicStr Method;
        internal RunicStr Target;
        internal RunicStr RemoteAddress;
        internal RunicStr LocalAddress;
        internal RunicHeader* Headers;
        internal nuint HeaderCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunicServerOptions
    {
        internal uint AbiVersion;
        internal uint Flags;
        internal ushort Port;
        internal ushort Reserved;
        internal uint WorkerThreads;
        internal ulong MaxMessageBytes;
        internal uint ReceiveWindow;
        internal void* Context;
        internal delegate* unmanaged[Cdecl]<void*, RunicRequest*, void> OnRequest;
        internal delegate* unmanaged[Cdecl]<void*, ulong, void> OnAborted;
        internal delegate* unmanaged[Cdecl]<void*, ulong, ulong, uint, byte*, nuint, void> OnMessage;
        internal delegate* unmanaged[Cdecl]<void*, ulong, int, byte*, nuint, void> OnClosed;
        internal delegate* unmanaged[Cdecl]<void*, ulong, int, void> OnCompleted;
    }

    [LibraryImport(Library, EntryPoint = "runic_server_abi_version")]
    internal static partial uint AbiVersionOfLibrary();

    [LibraryImport(Library, EntryPoint = "runic_server_last_error")]
    internal static partial nuint LastError(byte* buffer, nuint capacity);

    [LibraryImport(Library, EntryPoint = "runic_server_start")]
    internal static partial int Start(RunicServerOptions* options, nint* server, ushort* port);

    [LibraryImport(Library, EntryPoint = "runic_server_stop")]
    internal static partial void Stop(nint server);

    [LibraryImport(Library, EntryPoint = "runic_server_respond")]
    internal static partial int Respond(NativeServerHandle server, ulong id, ushort status, RunicHeader* headers,
        nuint headerCount, uint body, RunicStr filePath);

    [LibraryImport(Library, EntryPoint = "runic_server_write")]
    internal static partial int Write(NativeServerHandle server, ulong id, byte* data, nuint length, ulong token);

    [LibraryImport(Library, EntryPoint = "runic_server_end")]
    internal static partial int End(NativeServerHandle server, ulong id);

    [LibraryImport(Library, EntryPoint = "runic_server_abort")]
    internal static partial void Abort(NativeServerHandle server, ulong id);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_accept")]
    internal static partial int AcceptWebSocket(NativeServerHandle server, ulong id, RunicStr protocol,
        RunicHeader* headers, nuint headerCount);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_send")]
    internal static partial int Send(NativeServerHandle server, ulong connection, uint kind, byte* data, nuint length,
        ulong token);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_close")]
    internal static partial int Close(NativeServerHandle server, ulong connection, ushort code, RunicStr reason, ulong token);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_consume")]
    internal static partial void Consume(NativeServerHandle server, ulong connection, ulong message);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_abort")]
    internal static partial void AbortWebSocket(NativeServerHandle server, ulong connection);

    [LibraryImport(Library, EntryPoint = "runic_server_ws_release")]
    internal static partial void Release(NativeServerHandle server, ulong connection);

    // The calling thread's last error message from the library.
    internal static string LastErrorMessage()
    {
        Span<byte> buffer = stackalloc byte[512];
        nuint length;
        fixed (byte* pointer = buffer)
        {
            length = LastError(pointer, (nuint)buffer.Length);
        }
        return System.Text.Encoding.UTF8.GetString(buffer[..(int)Math.Min(length, (nuint)buffer.Length)]);
    }
}

// The server pointer. Every call holds a reference, so runic_server_stop runs only after the calls in progress ended.
// It also owns the callbacks' context, which it frees after the library stopped.
internal sealed class NativeServerHandle : SafeHandle
{
    private GCHandle _context;

    public NativeServerHandle() : base(nint.Zero, ownsHandle: true)
    {
    }

    internal NativeServerHandle(nint server, GCHandle context) : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(server);
        _context = context;
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeServerInterop.Stop(handle);
        if (_context.IsAllocated)
        {
            _context.Free();
        }
        return true;
    }
}
