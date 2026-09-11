using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

// Handwritten firewall comparison backend only. Production uses generated VARIANT.
// Windows x64 VARIANT layout, including the two-pointer BRECORD union member.
// These values are passed by value according to the native interface signatures.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] internal ushort Type;
    [FieldOffset(8)] internal nint Pointer;
    [FieldOffset(8)] internal int Integer;
    internal static Variant Int32(int value) => new() { Type = 3, Integer = value };
    internal static Variant String(nint value) => new() { Type = 8, Pointer = value };
}

internal sealed class BString : IDisposable
{
    internal nint Pointer { get; private set; }
    internal unsafe global::Windows.Win32.Foundation.BSTR Native => new((char*)Pointer);
    internal BString(string value) => Pointer = Marshal.StringToBSTR(value);
    public void Dispose()
    {
        if (Pointer == 0) return;
        Marshal.ZeroFreeBSTR(Pointer);
        Pointer = 0;
    }
}

internal static unsafe class Automation
{
    internal static void RequireX64()
    {
        NativeError.Windows();
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This administration COM binding currently supports Windows x64.");
    }

    internal static string GetString(ComObject value, int slot, string operation)
    {
        nint result = 0;
        try
        {
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)value.Slot(slot))(value.Pointer, &result), operation);
            return result == 0 ? "" : Marshal.PtrToStringBSTR(result);
        }
        finally { if (result != 0) Marshal.FreeBSTR(result); }
    }

    internal static int GetInt32(ComObject value, int slot, string operation)
    {
        int result;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)value.Slot(slot))(value.Pointer, &result), operation);
        return result;
    }

    internal static bool GetBoolean(ComObject value, int slot, string operation)
    {
        short result;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, short*, int>)value.Slot(slot))(value.Pointer, &result), operation);
        return result != 0;
    }

    internal static void SetBoolean(ComObject value, int slot, bool enabled, string operation) =>
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, short, int>)value.Slot(slot))(value.Pointer, enabled ? (short)-1 : (short)0), operation);

    internal static ComObject GetObject(ComObject value, int slot, string operation)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)value.Slot(slot))(value.Pointer, &result), operation);
        return ComObject.Own(result);
    }
}
