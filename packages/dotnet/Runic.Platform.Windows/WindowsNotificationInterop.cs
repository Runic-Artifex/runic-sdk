using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.Windows;

// Fixed WinRT ABI calls preserve NativeAOT support without a Windows-only managed TFM.
internal static unsafe partial class WindowsNotificationInterop
{
    internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
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
    { using var text = new HString(name); Guid iid = new(id); Check(RoGetActivationFactory(text.Handle, in iid, out var factory)); return factory; }
    internal static nint Activate(string name)
    { using var text = new HString(name); Check(RoActivateInstance(text.Handle, out var result)); return result; }
    internal static string ReadString(nint value)
    { var data = WindowsGetStringRawBuffer(value, out var length); return new string((char*)data, 0, checked((int)length)); }
    internal sealed class HString : IDisposable
    {
        internal nint Handle { get; }
        internal HString(string value) { Check(WindowsCreateString(value, (uint)value.Length, out var handle)); Handle = handle; }
        public void Dispose() => _ = WindowsDeleteString(Handle);
    }
    [LibraryImport("combase.dll")] internal static partial int RoInitialize(uint mode);
    [LibraryImport("combase.dll")] internal static partial void RoUninitialize();
    [LibraryImport("combase.dll")] private static partial int RoGetActivationFactory(nint name, in Guid iid, out nint result);
    [LibraryImport("combase.dll")] private static partial int RoActivateInstance(nint name, out nint result);
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int WindowsCreateString(string value, uint length, out nint result);
    [LibraryImport("combase.dll")] internal static partial int WindowsDeleteString(nint value);
    [LibraryImport("combase.dll")] private static partial nint WindowsGetStringRawBuffer(nint value, out uint length);
}

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
        finally { if (text != 0) _ = WindowsNotificationInterop.WindowsDeleteString(text); WindowsNotificationInterop.Release(typed); }
        return 0;
    }
}
