using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.ActiveDirectory;
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var status = PInvoke.NetGetJoinInformation(_computerName, out var name, out var state);
        if (status != 0) throw NativeError.Win32("Read computer join state", unchecked((int)status));
        try
        {
            byte* role = null;
            status = PInvoke.DsRoleGetPrimaryDomainInformation(_computerName, DSROLE_PRIMARY_DOMAIN_INFO_LEVEL.DsRolePrimaryDomainInfoBasic, ref role);
            if (status != 0) throw NativeError.Win32("Read computer domain role", unchecked((int)status));
            try { return new((DomainJoinState)state, name.ToString(), (ComputerDomainRole)((DSROLE_PRIMARY_DOMAIN_INFO_BASIC*)role)->MachineRole); }
            finally { PInvoke.DsRoleFreeMemory(role); }
        }
        finally { _ = PInvoke.NetApiBufferFree(name.Value); }
    }

    /// <summary>Discovers a DNS-named directory controller, with optional primary-controller and writable requirements.</summary>
    public unsafe DomainControllerInfo DiscoverController(string? domainName = null, bool requirePrimary = false, bool requireWritable = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        if (domainName is not null) NativeError.Text(domainName, nameof(domainName));
        var flags = 0x40000000u | 0x10u | (requirePrimary ? 0x80u : 0) | (requireWritable ? 0x1000u : 0);
        var status = PInvoke.DsGetDcName(_computerName, domainName, null, null, flags, out var pointer);
        if (status != 0) throw NativeError.Win32("Discover domain controller", unchecked((int)status));
        try
        {
            var value = pointer;
            return new(value->DomainControllerName.ToString(), value->DomainControllerAddress.ToString(), value->DomainControllerAddressType, value->DomainGuid,
                value->DomainName.ToString(), value->DnsForestName.ToString(), value->Flags, value->DcSiteName.ToString(), value->ClientSiteName.ToString());
        }
        finally { _ = PInvoke.NetApiBufferFree(pointer); }
    }

}
