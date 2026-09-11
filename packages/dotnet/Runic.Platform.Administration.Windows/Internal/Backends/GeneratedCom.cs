using Windows.Win32;
using Windows.Win32.System.Com;
namespace Runic.Platform.Administration.Windows.Internal.Backends;
[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
internal sealed unsafe class GeneratedCom : IDisposable
{
    internal nint Pointer { get; private set; }
    private GeneratedCom(nint pointer) => Pointer = pointer;
    internal static GeneratedCom Own(nint pointer) => pointer != 0 ? new(pointer) : throw NativeError.Win32("Read COM object", 13);
    internal static GeneratedCom FromResult(int status, nint pointer, string operation)
    {
        if (status < 0)
        {
            if (pointer != 0) ((IUnknown*)pointer)->Release();
            throw NativeError.HResult(operation, status);
        }
        return Own(pointer);
    }
    internal static GeneratedCom Create(Guid clsid, Guid iid)
    {
        void* pointer = null;
        var status = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &pointer);
        if (status.Value < 0)
        {
            if (pointer != null) ((IUnknown*)pointer)->Release();
            throw NativeError.HResult("Create COM object", status.Value);
        }
        return Own((nint)pointer);
    }
    internal GeneratedCom Query(Guid iid)
    {
        void* pointer = null;
        var status = ((IUnknown*)Pointer)->QueryInterface(&iid, &pointer);
        if (status.Value < 0)
        {
            if (pointer != null) ((IUnknown*)pointer)->Release();
            throw NativeError.HResult("Query COM interface", status.Value);
        }
        return Own((nint)pointer);
    }
    public void Dispose()
    {
        if (Pointer == 0) return;
        ((IUnknown*)Pointer)->Release(); Pointer = 0;
    }
}
