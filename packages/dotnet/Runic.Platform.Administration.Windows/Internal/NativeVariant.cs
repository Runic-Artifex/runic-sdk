using Windows.Win32.Foundation;
using Windows.Win32.System.Variant;
namespace Runic.Platform.Administration.Windows.Internal;
internal static unsafe class NativeVariant
{
    internal static VARIANT Int32(int value) => new() { vt = VARENUM.VT_I4, lVal = value };
    internal static VARIANT String(nint value) => new() { vt = VARENUM.VT_BSTR, bstrVal = new BSTR((char*)value) };
}
