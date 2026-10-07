using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.WinRT;

namespace Runic.Platform.Windows;

// Fixed WinRT ABI calls preserve NativeAOT support without a Windows-only managed TFM.
// The flat runtime and HSTRING functions use generated bindings; the WinRT projection
// (vtable slots, parameterized delegate) stays handwritten. See docs/interop-inventory.md.
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal static unsafe class WindowsNotificationInterop
{
    internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    // S_FALSE (already initialized) succeeds; RPC_E_CHANGED_MODE throws as before.
    internal static void InitializeRuntime() => Check(PInvoke.RoInitialize(RO_INIT_TYPE.RO_INIT_MULTITHREADED).Value);
    internal static void UninitializeRuntime() => PInvoke.RoUninitialize();
    internal static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
    internal static void Release(nint instance) { if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance); }
    internal static nint Query(nint instance, string id)
    {
        Guid iid = new(id); nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result)); return result;
    }
    internal static nint Get(nint instance, int slot)
    { nint result = 0; Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &result)); return result; }
    internal static nint Get(nint instance, int slot, nint argument)
    { nint result = 0; Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, slot))(instance, argument, &result)); return result; }
    internal static void Call(nint instance, int slot, nint argument) => Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(instance, slot))(instance, argument));
    internal static nint Factory(string name, string id)
    {
        using var text = new HString(name); Guid iid = new(id); void* factory = null;
        Check(PInvoke.RoGetActivationFactory(text.Value, &iid, &factory).Value); return (nint)factory;
    }
    internal static nint Activate(string name)
    {
        using var text = new HString(name); IInspectable* result = null;
        Check(PInvoke.RoActivateInstance(text.Value, &result).Value); return (nint)result;
    }
    internal static string ReadString(nint value)
    { uint length = 0; var data = PInvoke.WindowsGetStringRawBuffer(new HSTRING(value), &length); return new string(data.Value, 0, checked((int)length)); }
    internal static void DeleteString(nint value) => _ = PInvoke.WindowsDeleteString(new HSTRING(value));
    // Owns one HSTRING. The generated SafeHandle overload is not used because the handle is
    // passed by value through fixed WinRT ABI slots.
    internal sealed class HString : IDisposable
    {
        internal HSTRING Value { get; }
        internal nint Handle => Value;
        internal HString(string value)
        {
            HSTRING handle;
            fixed (char* text = value) Check(PInvoke.WindowsCreateString(text, (uint)value.Length, &handle).Value);
            Value = handle;
        }
        public void Dispose() => DeleteString(Handle);
    }
}

// Handwritten by necessity: TypedEventHandler<ToastNotification,IInspectable> is a WinRT
// parameterized delegate absent from Win32 metadata, so CsWin32 has no interface or IID to
// generate. The static unmanaged vtable keeps the callback NativeAOT-safe.
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal static unsafe class ToastActivationHandler
{
    [StructLayout(LayoutKind.Sequential)] private struct Instance { internal nint Vtable, Context; internal int References; }
    private static readonly nint Vtable = CreateVtable();
    // Parameterized WinRT IID (UUID v5), TypedEventHandler<ToastNotification,IInspectable>.
    private static readonly Guid HandlerId = new("82fc5297-0949-53f5-9fba-adb79390440c");
    private static nint CreateVtable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<Instance*, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<Instance*, nint, nint, int>)&Invoke;
        return (nint)table;
    }
    internal static nint Create(Action<string> action)
    {
        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        instance->Vtable = Vtable; instance->References = 1; instance->Context = GCHandle.ToIntPtr(GCHandle.Alloc(action)); return (nint)instance;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(Instance* self, Guid* iid, nint* result)
    {
        *result = 0;
        if (*iid != HandlerId && *iid != new Guid("00000000-0000-0000-C000-000000000046") && *iid != new Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")) return unchecked((int)0x80004002);
        Interlocked.Increment(ref self->References); *result = (nint)self; return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Instance* self)
    {
        int count = Interlocked.Decrement(ref self->References);
        if (count == 0) { GCHandle.FromIntPtr(self->Context).Free(); NativeMemory.Free(self); }
        return (uint)count;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(Instance* self, nint sender, nint args)
    {
        nint typed = 0, text = 0;
        try
        {
            typed = WindowsNotificationInterop.Query(args, "e3bf92f3-c197-436f-8265-0625824f8dac");
            text = WindowsNotificationInterop.Get(typed, 6);
            ((Action<string>)GCHandle.FromIntPtr(self->Context).Target!)(WindowsNotificationInterop.ReadString(text));
        }
        catch { /* No application callback may unwind into COM. */ }
        finally { if (text != 0) WindowsNotificationInterop.DeleteString(text); WindowsNotificationInterop.Release(typed); }
        return 0;
    }
}
