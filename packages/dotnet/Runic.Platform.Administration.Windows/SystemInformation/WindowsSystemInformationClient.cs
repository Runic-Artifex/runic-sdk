using Windows.Win32;
using Windows.Win32.System.SystemInformation;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.SystemInformation;

/// <summary>OS version and raw registry labels. RegistryProductName is a compatibility label and can say Windows 10 on Windows 11.</summary>
public sealed record WindowsOperatingSystemInfo(Version Version, string RegistryProductName, string DisplayVersion,
    ushort OperatingSystemLanguage, byte NativeProductType, ushort NativeSuiteMask, uint? UpdateBuildRevision);

/// <summary>BIOS information from an SMBIOS type-0 structure, without WMI.</summary>
public sealed record WindowsBiosInfo(string Vendor, string Version, string ReleaseDateText, DateOnly? ReleaseDate);

/// <summary>Read-only local machine inventory.</summary>
public interface IWindowsSystemInformationClient
{
    /// <summary>Reads the actual native OS version and installed product metadata.</summary>
    WindowsOperatingSystemInfo GetOperatingSystem();
    /// <summary>Reads the actual Windows inventory display name.</summary>
    Task<string> GetOperatingSystemNameAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads BIOS structures from the firmware's SMBIOS table.</summary>
    ImmutableArray<WindowsBiosInfo> GetBiosInformation();
    /// <summary>Reads native computer membership and domain role.</summary>
    DomainMembership GetDomainMembership();
}

/// <summary>Local machine information through Windows APIs and read-only registry access.</summary>
public sealed partial class WindowsSystemInformationClient : IWindowsSystemInformationClient
{
    /// <summary>Reads the OS display name from Windows inventory. Unlike the compatibility registry label, this distinguishes Windows 11.</summary>
    public Task<string> GetOperatingSystemNameAsync(CancellationToken cancellationToken = default) =>
        ComApartment.RunAsync(() =>
        {
            using var connection = new WmiConnection(".", @"root\cimv2", null, TimeSpan.FromSeconds(30));
            var rows = connection.Query("SELECT Caption FROM Win32_OperatingSystem", ["Caption"], cancellationToken);
            return rows.Length == 1 && rows[0]["Caption"] is string { Length: > 0 } name
                ? name : throw NativeError.Win32("Read OS display name", 13);
        }, cancellationToken);


    /// <inheritdoc/>
    public unsafe WindowsOperatingSystemInfo GetOperatingSystem()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        NativeError.Windows();
        OSVERSIONINFOEXW info = default;
        info.dwOSVersionInfoSize = (uint)sizeof(OSVERSIONINFOEXW);
        var status = global::Windows.Wdk.PInvoke.RtlGetVersion((OSVERSIONINFOW*)&info);
        if (status.Value != 0) throw NativeError.Win32("Read OS version", unchecked((int)PInvoke.RtlNtStatusToDosError(status)));
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            return new(new((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber),
                key?.GetValue("ProductName") as string ?? "",
                key?.GetValue("DisplayVersion") as string ?? "",
                PInvoke.GetSystemDefaultUILanguage(), info.wProductType, info.wSuiteMask,
                key?.GetValue("UBR") is int revision ? unchecked((uint)revision) : null);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new WindowsAdministrationException("Read OS metadata", AdministrationErrorCategory.AccessDenied,
                NativeErrorDomain.HResult, error.HResult, "Reading installed OS metadata was denied.", error);
        }
    }

    /// <inheritdoc/>
    public unsafe ImmutableArray<WindowsBiosInfo> GetBiosInformation()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        NativeError.Windows();
        const uint provider = 0x52534d42; // 'RSMB', as defined by GetSystemFirmwareTable.
        var length = PInvoke.GetSystemFirmwareTable((FIRMWARE_TABLE_PROVIDER)provider, 0, null, 0);
        if (length == 0) throw NativeError.Win32("Read SMBIOS size", Marshal.GetLastPInvokeError());
        if (length > 16 * 1024 * 1024) throw NativeError.Win32("Read SMBIOS size", 13);
        var bytes = new byte[length];
        fixed (byte* pointer = bytes)
        {
            var actual = PInvoke.GetSystemFirmwareTable((FIRMWARE_TABLE_PROVIDER)provider, 0, pointer, length);
            if (actual == 0) throw NativeError.Win32("Read SMBIOS table", Marshal.GetLastPInvokeError());
            if (actual > length) throw NativeError.Win32("Read changing SMBIOS table", 183);
            return ParseBios(bytes.AsSpan(0, checked((int)actual)));
        }
    }

    /// <inheritdoc/>
    public DomainMembership GetDomainMembership() => new WindowsDomainClient().GetMembership();

    internal static ImmutableArray<WindowsBiosInfo> ParseBios(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) throw NativeError.Win32("Parse SMBIOS header", 13);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (length > raw.Length - 8) throw NativeError.Win32("Parse SMBIOS length", 13);
        var table = raw.Slice(8, (int)length);
        var offset = 0;
        var result = ImmutableArray.CreateBuilder<WindowsBiosInfo>();
        while (offset < table.Length)
        {
            if (table.Length - offset < 4) throw NativeError.Win32("Parse SMBIOS structure", 13);
            var type = table[offset];
            var formattedLength = table[offset + 1];
            if (formattedLength < 4 || formattedLength > table.Length - offset) throw NativeError.Win32("Parse SMBIOS formatted length", 13);
            var end = offset + formattedLength;
            var stringEnd = end;
            while (stringEnd + 1 < table.Length && !(table[stringEnd] == 0 && table[stringEnd + 1] == 0)) stringEnd++;
            if (stringEnd + 1 >= table.Length) throw NativeError.Win32("Parse SMBIOS string set", 13);
            if (type == 0)
            {
                if (formattedLength < 9) throw NativeError.Win32("Parse SMBIOS BIOS structure", 13);
                var strings = table.Slice(end, stringEnd - end + 1);
                var releaseText = ReadSmbiosString(strings, table[offset + 8]);
                DateOnly? release = DateOnly.TryParseExact(releaseText, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
                result.Add(new(ReadSmbiosString(strings, table[offset + 4]), ReadSmbiosString(strings, table[offset + 5]), releaseText, release));
            }
            offset = stringEnd + 2;
            if (type == 127) break;
        }
        return result.ToImmutable();
    }

    private static string ReadSmbiosString(ReadOnlySpan<byte> strings, byte index)
    {
        if (index == 0) return "";
        var start = 0;
        for (var current = 1; start < strings.Length; current++)
        {
            var end = strings[start..].IndexOf((byte)0);
            if (end < 0) break;
            if (current == index) return System.Text.Encoding.UTF8.GetString(strings.Slice(start, end));
            start += end + 1;
        }
        throw NativeError.Win32("Read SMBIOS string index", 13);
    }

}
