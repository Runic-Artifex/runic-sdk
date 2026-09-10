using Windows.Win32;
using Windows.Win32.System.Com;
using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal sealed unsafe partial class ComObject : IDisposable
{
    internal nint Pointer { get; private set; }
    private ComObject(nint pointer) => Pointer = pointer;
    internal static ComObject Own(nint pointer) => pointer == 0 ? throw NativeError.Win32("Read COM object", 13) : new(pointer);
    internal static ComObject Create(Guid classId, Guid interfaceId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        void* result = null;
        var status = PInvoke.CoCreateInstance(&classId, null, CLSCTX.CLSCTX_INPROC_SERVER, &interfaceId, &result);
        return FromResult(status.Value, (nint)result, "Activate Windows COM component");
    }
    internal ComObject Query(Guid interfaceId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        void* result = null;
        var status = ((IUnknown*)Pointer)->QueryInterface(&interfaceId, &result);
        return FromResult(status.Value, (nint)result, "Query Windows COM interface");
    }
    internal static ComObject FromResult(int status, nint pointer, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        if (status < 0)
        {
            if (pointer != 0) ((IUnknown*)pointer)->Release();
            NativeError.Check(status, operation);
        }
        return Own(pointer);
    }
    // Only the retained handwritten firewall comparison backend uses numbered slots.
    internal nint Slot(int index)
    {
        ObjectDisposedException.ThrowIf(Pointer == 0, this);
        return (*(nint**)Pointer)[index];
    }
    public void Dispose()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        if (Pointer == 0) return;
        ((IUnknown*)Pointer)->Release();
        Pointer = 0;
    }
}
