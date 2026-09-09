using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.MacOS;

internal static partial class MacDesktopNative
{
    internal const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial nint Args(nint target, nint selector, nint first, nint second);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial nint Args3(nint target, nint selector, nint first, nint second, nint third);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial void Args4(nint target, nint selector, nint first, nint second, nint third, nint fourth);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial byte BoolArg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial double Double(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Utf8(nint target, nint selector, string value);
    internal static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";
    internal static nint String(string value) => Utf8(Class("NSString"), Sel("stringWithUTF8String:"), value);
    internal static nint Array(nint value) => Arg(Class("NSArray"), Sel("arrayWithObject:"), value);
    internal static nint Url(string path) => Arg(Class("NSURL"), Sel("fileURLWithPath:"), String(path));
    internal static void Release(nint value) { if (value != 0) Send(value, Sel("release")); }
    internal readonly struct Pool : IDisposable
    {
        private readonly nint _pool;
        public Pool() => _pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        public void Dispose() => Send(_pool, Sel("drain"));
    }
}

// Objective-C blocks own one GCHandle per native heap copy. No runtime-generated delegates.
internal static unsafe partial class MacDesktopBlock
{
    [StructLayout(LayoutKind.Sequential)] private struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke, Descriptor, Context; }
    [StructLayout(LayoutKind.Sequential)] private struct Descriptor { internal nuint Reserved, Size; internal nint Copy, Dispose, Signature; }
    private static readonly nint SystemLibrary = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
    private static readonly nint StackBlock = NativeLibrary.GetExport(SystemLibrary, "_NSConcreteStackBlock");
    private static readonly nint OneDescriptor = DescriptorFor("v@?@");
    private static readonly nint ResponseDescriptor = DescriptorFor("v@?q");
    private static readonly nint TwoDescriptor = DescriptorFor("v@?@@");
    private static readonly nint BoolDescriptor = DescriptorFor("v@?B@");
    private static nint DescriptorFor(string signature)
    {
        var d = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
        d->Size = (nuint)sizeof(Block); d->Copy = (nint)(delegate* unmanaged[Cdecl]<Block*, Block*, void>)&Copy;
        d->Dispose = (nint)(delegate* unmanaged[Cdecl]<Block*, void>)&Destroy;
        d->Signature = Marshal.StringToCoTaskMemUTF8(signature); return (nint)d;
    }
    internal static nint Create(Action<nint> callback) => Create((a, _) => callback(a), OneDescriptor, (nint)(delegate* unmanaged[Cdecl]<Block*, nint, void>)&One);
    internal static nint Create(Action<nint, nint> callback) => Create(callback, TwoDescriptor, (nint)(delegate* unmanaged[Cdecl]<Block*, nint, nint, void>)&Two);
    internal static nint Response(Action<nint> callback) => Create((a, _) => callback(a), ResponseDescriptor, (nint)(delegate* unmanaged[Cdecl]<Block*, nint, void>)&One);
    internal static nint Authorization(Action<nint, nint> callback) => Create(callback, BoolDescriptor, (nint)(delegate* unmanaged[Cdecl]<Block*, byte, nint, void>)&Boolean);
    private static nint Create(Action<nint, nint> callback, nint descriptor, nint invoke)
    {
        var handle = GCHandle.Alloc(callback);
        try
        {
            Block block = new() { Isa = StackBlock, Flags = (1 << 25) | (1 << 30), Invoke = invoke, Descriptor = descriptor, Context = GCHandle.ToIntPtr(handle) };
            var copy = BlockCopy((nint)(&block));
            return copy != 0 ? copy : throw new InvalidOperationException("Cocoa could not copy a completion block.");
        }
        finally { handle.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Copy(Block* to, Block* from) => to->Context = GCHandle.ToIntPtr(GCHandle.Alloc(GCHandle.FromIntPtr(from->Context).Target));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Destroy(Block* block) => GCHandle.FromIntPtr(block->Context).Free();
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void One(Block* b, nint a) => Run(b, a, 0);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Two(Block* b, nint a, nint c) => Run(b, a, c);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void Boolean(Block* b, byte a, nint c) => Run(b, a, c);
    private static void Run(Block* b, nint a, nint c)
    { try { ((Action<nint, nint>)GCHandle.FromIntPtr(b->Context).Target!)(a, c); } catch { /* Never unwind through Cocoa. */ } }
    internal static void Complete(nint block) => ((delegate* unmanaged[Cdecl]<nint, void>)((Block*)block)->Invoke)(block);
    internal static void Present(nint block) => ((delegate* unmanaged[Cdecl]<nint, nuint, void>)((Block*)block)->Invoke)(block, 16 | 8);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")] private static partial nint BlockCopy(nint block);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")] internal static partial void Release(nint block);
}
