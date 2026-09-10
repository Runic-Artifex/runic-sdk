using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>Machine join state.</summary>
public enum DomainJoinState
{
    /// <summary>Unknown.</summary>
    Unknown,
    /// <summary>Unjoined.</summary>
    Unjoined,
    /// <summary>Workgroup member.</summary>
    Workgroup,
    /// <summary>Domain member.</summary>
    Domain
}
/// <summary>Native computer domain role.</summary>
public enum ComputerDomainRole
{
    /// <summary>Standalone workstation.</summary>
    StandaloneWorkstation,
    /// <summary>Member workstation.</summary>
    MemberWorkstation,
    /// <summary>Standalone server.</summary>
    StandaloneServer,
    /// <summary>Member server.</summary>
    MemberServer,
    /// <summary>Backup domain controller.</summary>
    BackupDomainController,
    /// <summary>Primary domain controller.</summary>
    PrimaryDomainController
}
/// <summary>Machine join information; Name identifies either the domain or workgroup.</summary>
public sealed record DomainMembership(DomainJoinState State, string Name, ComputerDomainRole Role);
/// <summary>A controller returned by Windows domain discovery.</summary>
public sealed record DomainControllerInfo(string Name, string Address, uint AddressType, Guid DomainId,
    string DomainName, string ForestName, uint NativeFlags, string ControllerSite, string ClientSite);

/// <summary>Native machine membership and controller discovery, without ADSI.</summary>
public sealed partial class WindowsDomainClient
{
    private readonly string? _computerName;
    /// <summary>Creates a client for the local computer, or an explicit remote computer.</summary>
    public WindowsDomainClient(string? computerName = null)
    {
        NativeError.Windows();
        if (computerName is not null) NativeError.Text(computerName, nameof(computerName));
        _computerName = computerName;
    }

    /// <summary>Reads join state and domain role; a workgroup is a successful result, not a failed domain query.</summary>
    public unsafe DomainMembership GetMembership()
    {
        var status = NetGetJoinInformation(_computerName, out var name, out var state);
        if (status != 0) throw NativeError.Win32("Read computer join state", status);
        try
        {
            status = DsRoleGetPrimaryDomainInformation(_computerName, 1, out var role);
            if (status != 0) throw NativeError.Win32("Read computer domain role", status);
            try { return new((DomainJoinState)state, Marshal.PtrToStringUni(name) ?? "", (ComputerDomainRole)(*(uint*)role)); }
            finally { DsRoleFreeMemory(role); }
        }
        finally { _ = NetApiBufferFree(name); }
    }

    /// <summary>Discovers a DNS-named directory controller, with optional primary-controller and writable requirements.</summary>
    public unsafe DomainControllerInfo DiscoverController(string? domainName = null, bool requirePrimary = false, bool requireWritable = true)
    {
        if (domainName is not null) NativeError.Text(domainName, nameof(domainName));
        var flags = 0x40000000u | 0x10u | (requirePrimary ? 0x80u : 0) | (requireWritable ? 0x1000u : 0);
        var status = DsGetDcNameW(_computerName, domainName, 0, null, flags, out var pointer);
        if (status != 0) throw NativeError.Win32("Discover domain controller", status);
        try
        {
            var value = (Controller*)pointer;
            return new(Text(value->Name), Text(value->Address), value->AddressType, value->DomainId,
                Text(value->DomainName), Text(value->ForestName), value->Flags, Text(value->ControllerSite), Text(value->ClientSite));
        }
        finally { _ = NetApiBufferFree(pointer); }
    }

    private static string Text(nint pointer) => Marshal.PtrToStringUni(pointer) ?? "";

    [StructLayout(LayoutKind.Sequential)]
    private struct Controller
    {
        internal nint Name, Address;
        internal uint AddressType;
        internal Guid DomainId;
        internal nint DomainName, ForestName;
        internal uint Flags;
        internal nint ControllerSite, ClientSite;
    }
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out nint name, out int state);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsRoleGetPrimaryDomainInformation(string? server, int level, out nint buffer);
    [LibraryImport("netapi32.dll")] private static partial void DsRoleFreeMemory(nint buffer);
    [LibraryImport("netapi32.dll")] private static partial int NetApiBufferFree(nint buffer);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsGetDcNameW(string? computer, string? domain, nint domainGuid, string? site, uint flags, out nint info);
}
