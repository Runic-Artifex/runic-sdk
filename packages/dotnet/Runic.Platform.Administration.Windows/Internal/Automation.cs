using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

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

internal static class Automation
{
    internal static void RequireX64()
    {
        NativeError.Windows();
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This administration COM binding currently supports Windows x64.");
    }

    internal static unsafe ComObject GetObject(ComObject value, int slot, string operation)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)value.Slot(slot))(value.Pointer, &result), operation);
        return ComObject.Own(result);
    }
}
