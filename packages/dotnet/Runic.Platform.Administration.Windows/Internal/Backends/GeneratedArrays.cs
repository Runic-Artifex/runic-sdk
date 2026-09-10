using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Variant;
namespace Runic.Platform.Administration.Windows.Internal.Backends;
[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
internal static unsafe class GeneratedArrays
{
    internal static ImmutableArray<string> ReadStrings(VARIANT value)
    {
        if (value.vt is VARENUM.VT_EMPTY or VARENUM.VT_NULL) return [];
        if (((int)value.vt & 0x2000) == 0 || PInvoke.SafeArrayGetDim(value.parray) != 1)
            throw NativeError.Win32("Read native string array", 13);
        int lower = 0, upper = 0;
        NativeError.Check(PInvoke.SafeArrayGetLBound(value.parray, 1, &lower).Value, "Read array lower bound");
        NativeError.Check(PInvoke.SafeArrayGetUBound(value.parray, 1, &upper).Value, "Read array upper bound");
        var rows = ImmutableArray.CreateBuilder<string>();
        for (var i = lower; i <= upper; i++)
        {
            if (((int)value.vt & 0xfff) == (int)VARENUM.VT_BSTR)
            {
                BSTR text = default;
                try
                {
                    NativeError.Check(PInvoke.SafeArrayGetElement(value.parray, &i, &text).Value, "Read string array element");
                    rows.Add(text.Value == null ? "" : Marshal.PtrToStringBSTR((nint)text.Value));
                }
                finally { PInvoke.SysFreeString(text); }
            }
            else if (((int)value.vt & 0xfff) == (int)VARENUM.VT_VARIANT)
            {
                VARIANT item = default;
                try
                {
                    NativeError.Check(PInvoke.SafeArrayGetElement(value.parray, &i, &item).Value, "Read variant array element");
                    if (item.vt != VARENUM.VT_BSTR) throw NativeError.Win32("Read string array element", 13);
                    rows.Add(item.bstrVal.Value == null ? "" : Marshal.PtrToStringBSTR((nint)item.bstrVal.Value));
                }
                finally { _ = PInvoke.VariantClear(&item); }
            }
            else throw NativeError.Win32("Read native string array", 13);
        }
        return rows.ToImmutable();
    }
    internal static VARIANT CreateStrings(ImmutableArray<string> values)
    {
        var array = PInvoke.SafeArrayCreateVector(VARENUM.VT_VARIANT, 0, (uint)values.Length);
        if (array == null) throw NativeError.Win32("Allocate native array", 8);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                using var text = new BString(values[i]);
                VARIANT item = default; item.vt = VARENUM.VT_BSTR; item.bstrVal = new BSTR((char*)text.Pointer);
                NativeError.Check(PInvoke.SafeArrayPutElement(array, &i, &item).Value, "Write native array element");
            }
            VARIANT result = default; result.vt = (VARENUM)0x200c; result.parray = array; return result;
        }
        catch { _ = PInvoke.SafeArrayDestroy(array); throw; }
    }
}
