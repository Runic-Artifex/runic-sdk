// Used only by the handwritten firewall comparison backend. Production uses GeneratedArrays.
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal static unsafe partial class AutomationArrays
{
    internal static ImmutableArray<string> ReadStrings(Variant value)
    {
        if (value.Type == 0 || value.Type == 1) return [];
        if ((value.Type & 0x2000) == 0 || SafeArrayGetDim(value.Pointer) != 1)
            throw NativeError.Win32("Read native string array", 13);
        NativeError.Check(SafeArrayGetLBound(value.Pointer, 1, out var lower), "Read array lower bound");
        NativeError.Check(SafeArrayGetUBound(value.Pointer, 1, out var upper), "Read array upper bound");
        var rows = ImmutableArray.CreateBuilder<string>();
        for (var i = lower; i <= upper; i++)
        {
            if ((value.Type & 0xfff) == 8)
            {
                nint text = 0;
                try
                {
                    NativeError.Check(SafeArrayGetElement(value.Pointer, &i, &text), "Read string array element");
                    rows.Add(text == 0 ? "" : Marshal.PtrToStringBSTR(text));
                }
                finally { if (text != 0) Marshal.FreeBSTR(text); }
            }
            else if ((value.Type & 0xfff) == 12)
            {
                Variant item = default;
                try
                {
                    NativeError.Check(SafeArrayGetElement(value.Pointer, &i, &item), "Read variant array element");
                    if (item.Type != 8) throw NativeError.Win32("Read string array element", 13);
                    rows.Add(item.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(item.Pointer));
                }
                finally { _ = VariantClear(&item); }
            }
            else throw NativeError.Win32("Read native string array", 13);
        }
        return rows.ToImmutable();
    }

    internal static Variant CreateStrings(ImmutableArray<string> values)
    {
        var array = SafeArrayCreateVector(12, 0, (uint)values.Length);
        if (array == 0) throw NativeError.Win32("Allocate native array", 8);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                using var text = new BString(values[i]);
                var value = Variant.String(text.Pointer);
                NativeError.Check(SafeArrayPutElement(array, &i, &value), "Write native array element");
            }
            return new() { Type = 0x200c, Pointer = array };
        }
        catch { _ = SafeArrayDestroy(array); throw; }
    }

    [LibraryImport("oleaut32.dll")] internal static partial int VariantClear(Variant* value);
    [LibraryImport("oleaut32.dll")] private static partial uint SafeArrayGetDim(nint array);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayGetLBound(nint array, uint dimension, out int value);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayGetUBound(nint array, uint dimension, out int value);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayGetElement(nint array, int* indices, void* value);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayPutElement(nint array, int* indices, void* value);
    [LibraryImport("oleaut32.dll")] private static partial nint SafeArrayCreateVector(ushort type, int lower, uint count);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayDestroy(nint array);
}
