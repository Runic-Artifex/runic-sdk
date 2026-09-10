using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal sealed unsafe partial class ComObject : IDisposable
{
    internal nint Pointer { get; private set; }
    private ComObject(nint pointer) => Pointer = pointer;
    internal static ComObject Own(nint pointer) => pointer == 0 ? throw NativeError.Win32("Read COM object", 13) : new(pointer);
    internal static ComObject Create(Guid classId, Guid interfaceId)
    {
        NativeError.Check(CoCreateInstance(in classId, 0, 1, in interfaceId, out var result), "Activate Windows COM component");
        return new(result);
    }
    internal ComObject Query(Guid interfaceId)
    {
        nint result;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(0))(Pointer, &interfaceId, &result), "Query Windows COM interface");
        return new(result);
    }
    internal nint Slot(int index)
    {
        ObjectDisposedException.ThrowIf(Pointer == 0, this);
        return (*(nint**)Pointer)[index];
    }
    public void Dispose()
    {
        if (Pointer == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(2))(Pointer);
        Pointer = 0;
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint result);
}
