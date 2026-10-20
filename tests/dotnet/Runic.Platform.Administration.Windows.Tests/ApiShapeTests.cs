using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Dns;
using Runic.Platform.Administration.Windows.Internal;
using Runic.Platform.Administration.Windows.Services;
using Runic.Platform.Administration.Windows.Shares;
using Runic.Platform.Administration.Windows.Tasks;
using TUnit.Core;

namespace Runic.Platform.Administration.Windows.Tests;

// The 0.9 API decisions (W319-003 B9): error categories, the async rule for remote clients, closed unions,
// DNS addresses and TTLs, and typed directory reads. These cases call no native API, so they run on every
// operating system; the attribute only satisfies the platform analyzer for the Windows-only assembly.
[SupportedOSPlatform("windows")]
internal sealed class ApiShapeTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public void ZeroIsNeverARealErrorCategory()
    {
        Check(default(AdministrationErrorCategory) == AdministrationErrorCategory.Unknown, "The default category must be Unknown.");
        int[] values = [.. Enum.GetValues<AdministrationErrorCategory>().Select(value => (int)value)];
        Check(values.SequenceEqual([0, 1, 2, 3, 4, 5, 6]), "The category numbering changed: " + string.Join(",", values));
        Check((int)AdministrationErrorCategory.NotFound == 2 && (int)AdministrationErrorCategory.NativeFailure == 6, "Unexpected category values.");
    }

    [Test]
    public void MissingTargetsAreNotFound()
    {
        // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, ERROR_SERVICE_DOES_NOT_EXIST, NERR_NetNameNotFound,
        // ERROR_DS_NO_SUCH_OBJECT and WBEM_E_NOT_FOUND.
        foreach (var code in new[] { 2, 3, 1060, 2310, 8240, unchecked((int)0x80041002) })
            Check(NativeError.Categorize(code) == AdministrationErrorCategory.NotFound, $"0x{code:X8} must be NotFound.");
        Check(NativeError.HResult("Read task", unchecked((int)0x80070002)).Category == AdministrationErrorCategory.NotFound,
            "A Win32 not-found HRESULT must be NotFound.");
        Check(NativeError.HResult("Read GPO", unchecked((int)0x80072030)).Category == AdministrationErrorCategory.NotFound,
            "A missing directory object reported by GPMC must be NotFound.");
        Check(WindowsDirectoryClient.Categorize(32) == AdministrationErrorCategory.NotFound, "LDAP noSuchObject must be NotFound.");
    }

    [Test]
    public void UnreachableEndpointsAndComponentsStayUnavailable()
    {
        // Network path, RPC server, class not registered (GPMC missing), WMI invalid namespace and class.
        foreach (var code in new[] { 53, 64, 67, 1722, 1726, unchecked((int)0x80040154), unchecked((int)0x8004100E), unchecked((int)0x80041010) })
            Check(NativeError.Categorize(code) == AdministrationErrorCategory.Unavailable, $"0x{code:X8} must be Unavailable.");
        Check(WindowsDirectoryClient.Categorize(52) == AdministrationErrorCategory.Unavailable, "LDAP unavailable must stay Unavailable.");
        Check(NativeError.Categorize(5) == AdministrationErrorCategory.AccessDenied, "ERROR_ACCESS_DENIED must be AccessDenied.");
        Check(NativeError.Categorize(31) == AdministrationErrorCategory.NativeFailure, "An unclassified code must be NativeFailure.");
        Check(WindowsDirectoryClient.Categorize(80) == AdministrationErrorCategory.NativeFailure, "An unclassified LDAP code must be NativeFailure.");
    }

    [Test]
    public void RemoteClientsExposeOnlyCancellableAsyncOperations()
    {
        CheckAsyncOnly(typeof(WindowsServiceClient));
        CheckAsyncOnly(typeof(WindowsShareClient));
        CheckAsyncOnly(typeof(WindowsDomainClient));
    }

    private static void CheckAsyncOnly([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Check(typeof(Task).IsAssignableFrom(method.ReturnType) && method.Name.EndsWith("Async", StringComparison.Ordinal),
                $"{type.Name}.{method.Name} must be an …Async operation.");
            var last = method.GetParameters().LastOrDefault();
            Check(last?.ParameterType == typeof(CancellationToken) && last.HasDefaultValue,
                $"{type.Name}.{method.Name} must take an optional CancellationToken last.");
        }
    }

    [Test]
    public void ClientsImplementNoPublicInterfaces()
    {
        CheckNoInterfaces(typeof(Firewall.WindowsFirewallClient));
        CheckNoInterfaces(typeof(Networks.WindowsNetworkClient));
        CheckNoInterfaces(typeof(Processes.WindowsProcessClient));
        CheckNoInterfaces(typeof(Shortcuts.WindowsShellLinkClient));
        CheckNoInterfaces(typeof(SystemInformation.WindowsSystemInformationClient));
    }

    private static void CheckNoInterfaces([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type) =>
        Check(type.GetInterfaces().Length == 0, type.Name + " implements a public client interface.");

    [Test]
    public void TaskTriggerIsClosed()
    {
        var constructors = typeof(TaskTrigger).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var parameterless = constructors.Single(constructor => constructor.GetParameters().Length == 0);
        Check(parameterless.IsFamilyAndAssembly, "TaskTrigger's constructor must be private protected.");
    }

    [Test]
    public void DnsAddressesCompareByValue()
    {
        var left = new DnsRecordKey("example.test", "host.example.test", new DnsRecordData.Aaaa(IPAddress.Parse("2001:db8::1")));
        var right = new DnsRecordKey("example.test", "host.example.test", new DnsRecordData.Aaaa(IPAddress.Parse("2001:0db8:0:0::1")));
        Check(left == right && left.GetHashCode() == right.GetHashCode(), "Equivalent IPv6 spellings must be one record key.");
        Check(WindowsDnsClient.Address(IPAddress.Parse("198.51.100.1"), AddressFamily.InterNetwork) == "198.51.100.1", "IPv4 text changed.");
        foreach (var (address, family) in new[] { (IPAddress.IPv6Loopback, AddressFamily.InterNetwork), (IPAddress.Loopback, AddressFamily.InterNetworkV6) })
        {
            try { _ = WindowsDnsClient.Address(address, family); throw new InvalidOperationException("A mismatched address family was accepted."); }
            catch (ArgumentException) { }
        }
        Check(new DnsRecordData.Ptr("target.example.test") is DnsRecordData, "PTR record data missing.");
    }

    // The MicrosoftDNS provider deletes a record when Modify receives its unchanged data, and its
    // SRV class names the target SRVDomainName; DomainName is the zone (W319-009 domain acceptance).
    [Test]
    public void DnsModifySendsOnlyChangedData()
    {
        var ttlOnly = WindowsDnsClient.ModifyValues(new DnsRecordData.A(IPAddress.Parse("198.51.100.1")), dataChanges: false, 600);
        Check(ttlOnly.Count == 1 && ttlOnly["TTL"] is 600u, "A TTL-only update must send only the TTL.");
        var address = WindowsDnsClient.ModifyValues(new DnsRecordData.A(IPAddress.Parse("198.51.100.2")), dataChanges: true, 300);
        Check(address.Count == 2 && (string)address["IPAddress"] == "198.51.100.2" && address["TTL"] is 300u, "A changed address must be sent with the TTL.");
        var service = WindowsDnsClient.ModifyValues(new DnsRecordData.Srv(10, 20, 443, "target.example.test"), dataChanges: true, 300);
        Check(service.Count == 5 && (string)service["SRVDomainName"] == "target.example.test" && !service.ContainsKey("DomainName")
            && service["Priority"] is 10u && service["Weight"] is 20u && service["Port"] is 443u, "SRV create/modify input must name its target SRVDomainName.");
        Check(WindowsDnsClient.DataEquals(new DnsRecordData.CName("Target.Example.Test."), new DnsRecordData.CName("target.example.test")),
            "Equivalent DNS names must not count as a data change.");
    }

    [Test]
    public void DnsTimeToLiveIsWholeSeconds()
    {
        Check(new DnsRecordSpecification(new("z", "o", new DnsRecordData.Txt("t"))).TimeToLive == TimeSpan.FromHours(1), "The default TTL must stay one hour.");
        Check(WindowsDnsClient.TimeToLiveSeconds(TimeSpan.FromMinutes(5), "ttl") == 300, "TTL conversion failed.");
        Check(WindowsDnsClient.TimeToLiveSeconds(TimeSpan.Zero, "ttl") == 0, "A zero TTL is valid.");
        Check(WindowsDnsClient.TimeToLiveSeconds(TimeSpan.FromSeconds(uint.MaxValue), "ttl") == uint.MaxValue, "The largest TTL is valid.");
        foreach (var invalid in new[] { TimeSpan.FromSeconds(-1), TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(uint.MaxValue + 1L) })
        {
            try { _ = WindowsDnsClient.TimeToLiveSeconds(invalid, "ttl"); throw new InvalidOperationException("Invalid TTL accepted: " + invalid); }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    [Test]
    public void GroupPolicyTimesCarryTheLocalOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Runic+02", TimeSpan.FromHours(2), "Runic+02", "Runic+02");
        var value = NativeError.LocalDate(new DateTime(2026, 10, 20, 12, 30, 0, DateTimeKind.Unspecified), zone);
        Check(value.Offset == TimeSpan.FromHours(2) && value.UtcDateTime == new DateTime(2026, 10, 20, 10, 30, 0, DateTimeKind.Utc),
            "GPMC local time must keep this computer's offset.");
    }

    [Test]
    public void DirectoryTextReadsAreTyped()
    {
        ImmutableArray<DirectoryValue> values = [new DirectoryValue.Text("CN=User"), new DirectoryValue.Binary([.. "HTTP/host"u8])];
        Check(ActiveDirectoryClient.ReadText(values).SequenceEqual(["CN=User", "HTTP/host"]), "Directory text values were not decoded.");
        try { _ = ActiveDirectoryClient.ReadText([new DirectoryValue.Binary([0xff, 0xfe])]); throw new InvalidOperationException("Invalid UTF-8 accepted."); }
        catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.InvalidData, "Invalid UTF-8 must be InvalidData."); }
    }
}
