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
/// <param name="State">The join state.</param>
/// <param name="Name">The flat (NetBIOS) name of the domain or workgroup.</param>
/// <param name="Role">The computer's role.</param>
public sealed record DomainMembership(DomainJoinState State, string Name, ComputerDomainRole Role)
{
    /// <summary>The DNS name of the joined domain, read locally without contacting a controller; null for a workgroup
    /// or a domain without a DNS name.</summary>
    public string? DnsDomainName { get; init; }
    /// <summary>The DNS name of the joined domain's forest; null when <see cref="DnsDomainName"/> is null.</summary>
    public string? ForestName { get; init; }
}
/// <summary>The form of the names domain controller discovery returns.</summary>
public enum DomainControllerNameFormat
{
    /// <summary>DNS names (<c>DS_RETURN_DNS_NAME</c>), such as <c>\\dc1.example.test</c> and <c>example.test</c>.</summary>
    Dns = 0,
    /// <summary>Flat (NetBIOS) names (<c>DS_RETURN_FLAT_NAME</c>), such as <c>\\DC1</c> and <c>EXAMPLE</c>. The forest
    /// name stays a DNS name.</summary>
    Flat = 1
}
/// <summary>A controller returned by Windows domain discovery.</summary>
/// <remarks>NativeAddressType is the native DS_INET_ADDRESS (1) or DS_NETBIOS_ADDRESS (2) value; NativeFlags are the native DS_*_FLAG bits.</remarks>
public sealed record DomainControllerInfo(string Name, string Address, uint NativeAddressType, Guid DomainId,
    string DomainName, string ForestName, uint NativeFlags, string ControllerSite, string ClientSite);

/// <summary>Native machine membership and controller discovery, without ADSI.</summary>
/// <remarks>Native NetAPI and DC-locator calls run off the caller's thread. Cancellation before a call starts
/// prevents it; once a call starts, its actual result is returned.</remarks>
public sealed partial class WindowsDomainClient
{
    private readonly string? _machineName;
    /// <summary>Creates a client for the local computer, or an explicit remote computer.</summary>
    public WindowsDomainClient(string? machineName = null)
    {
        NativeError.Windows();
        if (machineName is not null) NativeError.Text(machineName, nameof(machineName));
        _machineName = machineName;
    }

    /// <summary>Reads join state and domain role; a workgroup is a successful result, not a failed domain query.</summary>
    public Task<DomainMembership> GetMembershipAsync(CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(GetMembership, cancellationToken);

    /// <summary>Discovers a DNS-named directory controller, with optional primary-controller and writable requirements.</summary>
    /// <remarks>Discovery can block for the DC locator's network timeouts.</remarks>
    public Task<DomainControllerInfo> DiscoverControllerAsync(string? domainName = null, bool requirePrimary = false, bool requireWritable = true,
        CancellationToken cancellationToken = default) =>
        DiscoverControllerAsync(domainName, requirePrimary, requireWritable, DomainControllerNameFormat.Dns, cancellationToken);

    /// <summary>Discovers a directory controller and returns its names in the requested form.</summary>
    /// <remarks>With <see cref="DomainControllerNameFormat.Flat"/>, <see cref="DomainControllerInfo.Name"/> is the
    /// controller's NetBIOS name (as <c>Win32_NTDomain.DomainControllerName</c> reported it) and
    /// <see cref="DomainControllerInfo.DomainName"/> the domain's flat name. Each call is one locator query, so two
    /// calls with different formats can return different controllers. Discovery can block for the DC locator's network
    /// timeouts.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "This overload requires the name format, so a call that omits it resolves to the 0.13 overload unchanged.")]
    public Task<DomainControllerInfo> DiscoverControllerAsync(string? domainName, bool requirePrimary, bool requireWritable,
        DomainControllerNameFormat nameFormat, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(nameFormat)) throw new ArgumentOutOfRangeException(nameof(nameFormat));
        return NativeWork.RunAsync(() => DiscoverController(domainName, requirePrimary, requireWritable, nameFormat), cancellationToken);
    }

    private unsafe DomainMembership GetMembership()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var status = PInvoke.NetGetJoinInformation(_machineName, out var name, out var state);
        if (status != 0) throw NativeError.Win32("Read computer join state", unchecked((int)status));
        try
        {
            byte* role = null;
            status = PInvoke.DsRoleGetPrimaryDomainInformation(_machineName, DSROLE_PRIMARY_DOMAIN_INFO_LEVEL.DsRolePrimaryDomainInfoBasic, ref role);
            if (status != 0) throw NativeError.Win32("Read computer domain role", unchecked((int)status));
            try
            {
                var basic = (DSROLE_PRIMARY_DOMAIN_INFO_BASIC*)role;
                var joinState = (DomainJoinState)state;
                // A workgroup computer reports its workgroup as the flat name and no DNS name.
                var dns = joinState == DomainJoinState.Domain ? Optional(basic->DomainNameDns) : null;
                return new(joinState, name.ToString(), (ComputerDomainRole)basic->MachineRole)
                {
                    DnsDomainName = dns,
                    ForestName = dns is null ? null : Optional(basic->DomainForestName)
                };
            }
            finally { PInvoke.DsRoleFreeMemory(role); }
        }
        finally { _ = PInvoke.NetApiBufferFree(name.Value); }
    }

    private static unsafe string? Optional(PWSTR value) => value.Value == null || *value.Value == '\0' ? null : value.ToString();

    private unsafe DomainControllerInfo DiscoverController(string? domainName, bool requirePrimary, bool requireWritable, DomainControllerNameFormat nameFormat)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        if (domainName is not null) NativeError.Text(domainName, nameof(domainName));
        // DS_RETURN_DNS_NAME or DS_RETURN_FLAT_NAME, DS_DIRECTORY_SERVICE_REQUIRED, DS_PDC_REQUIRED, DS_WRITABLE_REQUIRED.
        var flags = (nameFormat == DomainControllerNameFormat.Flat ? 0x80000000u : 0x40000000u) | 0x10u | (requirePrimary ? 0x80u : 0) | (requireWritable ? 0x1000u : 0);
        var status = PInvoke.DsGetDcName(_machineName, domainName, null, null, flags, out var pointer);
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
