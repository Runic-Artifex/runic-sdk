using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal static class NativeError
{
    internal static WindowsAdministrationException Win32(string operation, int code) =>
        Create(operation, NativeErrorDomain.Win32, code, code);

    internal static WindowsAdministrationException HResult(string operation, int code, Exception? inner = null) =>
        Create(operation, NativeErrorDomain.HResult, code,
            (code & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? code & 0xffff : code, inner);

    private static WindowsAdministrationException Create(string operation, NativeErrorDomain domain, int code, int normalized, Exception? inner = null)
    {
        var category = normalized switch
        {
            5 or 1314 or 1326 or unchecked((int)0x80041003) => AdministrationErrorCategory.AccessDenied,
            2 or 3 or 53 or 64 or 67 or 1060 or 1722 or 1726 => AdministrationErrorCategory.Unavailable,
            80 or 183 or 1056 or 1072 or 1073 => AdministrationErrorCategory.Conflict,
            13 or 24 or 87 or unchecked((int)0x80041318) or unchecked((int)0x8004131D) => AdministrationErrorCategory.InvalidData,
            unchecked((int)0x80040154) or unchecked((int)0x80041315) or unchecked((int)0x8004100E) or unchecked((int)0x80041010) => AdministrationErrorCategory.Unavailable,
            _ => AdministrationErrorCategory.NativeFailure
        };
        return new(operation, category, domain, code, $"{operation} failed ({domain}: 0x{code:X8}).", inner);
    }

    internal static Guid ParseGuid(string value) =>
        Guid.TryParse(value, out var result) ? result : throw Win32("Read native GUID", 13);

    internal static DateTime Date(double value)
    {
        try { return DateTime.FromOADate(value); }
        catch (ArgumentException error)
        {
            throw new WindowsAdministrationException("Read native timestamp", AdministrationErrorCategory.InvalidData,
                NativeErrorDomain.Win32, 13, "Windows returned an invalid timestamp.", error);
        }
    }

    internal static void Check(int hresult, string operation)
    {
        if (hresult < 0) throw HResult(operation, hresult);
    }

    internal static void CheckWin32(int result, string operation)
    {
        if (result == 0) throw Win32(operation, Marshal.GetLastPInvokeError());
    }

    internal static void Windows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows administration requires Windows.");
    }

    internal static void Text(string value, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
            throw new ArgumentException("The value must be valid text without NUL characters.", parameterName);
    }
}
