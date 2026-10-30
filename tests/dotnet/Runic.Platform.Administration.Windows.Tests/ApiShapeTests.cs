using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using Runic.Platform.Administration.Windows.Accounts;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Dns;
using Runic.Platform.Administration.Windows.Firewall;
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
        Check(values.SequenceEqual([0, 1, 2, 3, 4, 5, 6, 7]), "The category numbering changed: " + string.Join(",", values));
        Check((int)AdministrationErrorCategory.NotFound == 2 && (int)AdministrationErrorCategory.NativeFailure == 6
            && (int)AdministrationErrorCategory.NotInstalled == 7, "Unexpected category values.");
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
    public void UnreachableEndpointsStayUnavailable()
    {
        // Network path, RPC server and the Task Scheduler service not running.
        foreach (var code in new[] { 53, 64, 67, 1722, 1726, unchecked((int)0x80041315) })
            Check(NativeError.Categorize(code) == AdministrationErrorCategory.Unavailable, $"0x{code:X8} must be Unavailable.");
        Check(WindowsDirectoryClient.Categorize(52) == AdministrationErrorCategory.Unavailable, "LDAP unavailable must stay Unavailable.");
        Check(NativeError.Categorize(5) == AdministrationErrorCategory.AccessDenied, "ERROR_ACCESS_DENIED must be AccessDenied.");
        Check(NativeError.Categorize(31) == AdministrationErrorCategory.NativeFailure, "An unclassified code must be NativeFailure.");
        Check(WindowsDirectoryClient.Categorize(80) == AdministrationErrorCategory.NativeFailure, "An unclassified LDAP code must be NativeFailure.");
    }

    // sdk#744: a component that is not installed is reported apart from one that cannot be reached.
    [Test]
    public void MissingComponentsAreNotInstalled()
    {
        // Class not registered (GPMC missing), WMI invalid namespace and invalid class (a provider such as MicrosoftDNS).
        foreach (var code in new[] { unchecked((int)0x80040154), unchecked((int)0x8004100E), unchecked((int)0x80041010) })
            Check(NativeError.Categorize(code) == AdministrationErrorCategory.NotInstalled, $"0x{code:X8} must be NotInstalled.");
        var error = NativeError.HResult("Create GPMC", unchecked((int)0x80040154));
        Check(error.Category == AdministrationErrorCategory.NotInstalled && error.NativeErrorCode == unchecked((int)0x80040154),
            "A class-not-registered HRESULT must be NotInstalled and keep its code.");
    }

    // sdk#738: "already a member" and "not a member" are results; other statuses keep their native identity.
    [Test]
    public void LocalGroupMembershipStatusesAreResults()
    {
        Check(WindowsLocalGroupClient.MembershipResult(0, add: true) && WindowsLocalGroupClient.MembershipResult(0, add: false), "Success must be true.");
        Check(!WindowsLocalGroupClient.MembershipResult(1378, add: true), "ERROR_MEMBER_IN_ALIAS must be a false add.");
        Check(!WindowsLocalGroupClient.MembershipResult(1377, add: false), "ERROR_MEMBER_NOT_IN_ALIAS must be a false removal.");
        foreach (var (status, add, category) in new[] { (1377u, true, AdministrationErrorCategory.NativeFailure), (1378u, false, AdministrationErrorCategory.NativeFailure),
            (2220u, true, AdministrationErrorCategory.NotFound), (1376u, false, AdministrationErrorCategory.NotFound), (1387u, true, AdministrationErrorCategory.NotFound),
            (1332u, true, AdministrationErrorCategory.NotFound), (1388u, true, AdministrationErrorCategory.InvalidData), (5u, true, AdministrationErrorCategory.AccessDenied) })
        {
            try { _ = WindowsLocalGroupClient.MembershipResult(status, add); throw new InvalidOperationException($"Status {status} was a result."); }
            catch (WindowsAdministrationException error)
            {
                Check(error.Category == category && error.NativeErrorCode == (int)status && error.NativeErrorDomain == NativeErrorDomain.Win32,
                    $"Status {status} must be {category}, not {error.Category}.");
            }
        }
        Check(new AccountReference.Name(@"EXAMPLE\Domain Admins") == new AccountReference.Name(@"EXAMPLE\Domain Admins"), "Account names must compare by value.");
        WindowsLocalGroupClient.Validate(new AccountReference.Name("Administrators"), "group");
        foreach (var invalid in new AccountReference[] { new AccountReference.Name(""), new AccountReference.Name("a\0b"), new AccountReference.Sid(null!) })
        {
            try { WindowsLocalGroupClient.Validate(invalid, "group"); throw new InvalidOperationException("An invalid account reference was accepted: " + invalid); }
            catch (ArgumentException) { }
        }
    }

    [Test]
    public void RemoteClientsExposeOnlyCancellableAsyncOperations()
    {
        CheckAsyncOnly(typeof(WindowsLocalGroupClient));
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

    // sdk#728: resolver queries against a chosen server.
    [Test]
    public void DnsReverseLookupNames()
    {
        Check(WindowsDnsResolverClient.ReverseLookupName(IPAddress.Parse("192.0.2.10")) == "10.2.0.192.in-addr.arpa", "IPv4 reverse name changed.");
        Check(WindowsDnsResolverClient.ReverseLookupName(IPAddress.Parse("::ffff:192.0.2.10")) == "10.2.0.192.in-addr.arpa", "A mapped address must use its IPv4 name.");
        Check(WindowsDnsResolverClient.ReverseLookupName(IPAddress.Parse("2001:db8::567:89ab")) ==
            "b.a.9.8.7.6.5.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa", "IPv6 reverse name changed.");
    }

    [Test]
    public void DnsServerSocketAddressNamesNoPort()
    {
        var v4 = new byte[32];
        WindowsDnsResolverClient.WriteSocketAddress(IPAddress.Parse("192.0.2.10"), v4);
        Check(v4.AsSpan(0, 8).SequenceEqual(new byte[] { 2, 0, 0, 0, 192, 0, 2, 10 }) && v4.AsSpan(8).IndexOfAnyExcept((byte)0) < 0, "SOCKADDR_IN changed.");
        var v6 = new byte[32];
        WindowsDnsResolverClient.WriteSocketAddress(IPAddress.Parse("2001:db8::1%7"), v6);
        Check(v6.AsSpan(0, 8).SequenceEqual(new byte[] { 23, 0, 0, 0, 0, 0, 0, 0 }) && v6[8] == 0x20 && v6[9] == 0x01 && v6[23] == 1 && v6[24] == 7,
            "SOCKADDR_IN6 changed.");
    }

    [Test]
    public unsafe void DnsQueryStatusesAreResponseCodes()
    {
        Check(WindowsDnsResolverClient.Resolution(0, null) == new DnsResolution(DnsResponseCode.NoError, []), "Success without records.");
        Check(WindowsDnsResolverClient.Resolution(9501, null).ResponseCode == DnsResponseCode.NoError, "DNS_INFO_NO_RECORDS must be NoError.");
        Check(WindowsDnsResolverClient.Resolution(9003, null).ResponseCode == DnsResponseCode.NameError, "DNS_ERROR_RCODE_NAME_ERROR must be NameError.");
        Check(WindowsDnsResolverClient.Resolution(9002, null).ResponseCode == DnsResponseCode.ServerFailure, "SERVFAIL changed.");
        Check(WindowsDnsResolverClient.Resolution(9005, null).ResponseCode == DnsResponseCode.Refused, "REFUSED changed.");
        try { _ = WindowsDnsResolverClient.Resolution(1460, null); throw new InvalidOperationException("A timeout was a result."); }
        catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.Unavailable && error.NativeErrorCode == 1460, "A timeout must be Unavailable."); }
    }

    [Test]
    public unsafe void DnsAnswerRecordsAreTyped()
    {
        var size = sizeof(global::Windows.Win32.NetworkManagement.Dns.DNS_RECORDW) + 64;
        var memory = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(size * 5));
        fixed (char* owner = "alias.example.test", target = "host.example.test", first = "v=spf1 -all", second = "say \"hi\"")
        {
            try
            {
                var records = new global::Windows.Win32.NetworkManagement.Dns.DNS_RECORDW*[5];
                for (var i = 0; i < 5; i++) records[i] = (global::Windows.Win32.NetworkManagement.Dns.DNS_RECORDW*)(memory + i * size);
                for (var i = 0; i < 4; i++) records[i]->pNext = records[i + 1];
                foreach (var record in records) { record->pName = new(owner); record->dwTtl = 300; record->Flags.DW = 1; }
                records[0]->wType = 5; records[0]->Data.CNAME.pNameHost = new(target);
                records[1]->wType = 1; records[1]->pName = new(target); records[1]->Data.A.IpAddress = BitConverter.ToUInt32([192, 0, 2, 10]);
                records[2]->wType = 16; records[2]->Data.TXT.dwStringCount = 2;
                var strings = (global::Windows.Win32.Foundation.PWSTR*)&records[2]->Data.TXT.pStringArray;
                strings[0] = new(first); strings[1] = new(second);
                records[3]->wType = 6; // SOA, without a typed form
                records[4]->wType = 1; records[4]->Flags.DW = 2; // the authority section is not an answer
                var answers = WindowsDnsResolverClient.Answers(records[0]);
                Check(answers.Length == 4, "Expected four answers: " + string.Join(", ", answers));
                Check(answers[0] == new DnsAnswer("alias.example.test", DnsRecordType.CName, TimeSpan.FromMinutes(5), new DnsRecordData.CName("host.example.test")), "CNAME: " + answers[0]);
                Check(answers[1] == new DnsAnswer("host.example.test", DnsRecordType.A, TimeSpan.FromMinutes(5), new DnsRecordData.A(IPAddress.Parse("192.0.2.10"))), "A: " + answers[1]);
                Check(answers[2].Data == new DnsRecordData.Txt("\"v=spf1 -all\" \"say \\\"hi\\\"\""), "TXT: " + answers[2].Data);
                Check(answers[3].Type == (DnsRecordType)6 && answers[3].Data == new DnsRecordData.Unsupported("TYPE6", ""), "SOA: " + answers[3]);
            }
            finally { System.Runtime.InteropServices.NativeMemory.Free(memory); }
        }
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

    // Reads are typed by schema syntax (#447): string syntaxes are text; octet string, SID, security
    // descriptor and unknown syntaxes are binary.
    [Test]
    public void DirectorySchemaSyntaxSelectsTheValueType()
    {
        foreach (var (syntax, om) in new[] { ("2.5.5.1", 127), ("2.5.5.2", 6), ("2.5.5.3", 27), ("2.5.5.4", 20), ("2.5.5.5", 22), ("2.5.5.6", 18),
            ("2.5.5.7", 127), ("2.5.5.8", 1), ("2.5.5.9", 2), ("2.5.5.9", 10), ("2.5.5.11", 23), ("2.5.5.11", 24), ("2.5.5.12", 64), ("2.5.5.13", 127),
            ("2.5.5.14", 127), ("2.5.5.16", 65) })
            Check(!WindowsDirectoryClient.IsBinarySyntax(syntax, om), $"Syntax {syntax}/{om} must be text.");
        foreach (var (syntax, om) in new[] { ("2.5.5.10", 4), ("2.5.5.10", 127), ("2.5.5.15", 66), ("2.5.5.17", 4), ("2.5.5.99", 64), ("", 0) })
            Check(WindowsDirectoryClient.IsBinarySyntax(syntax, om), $"Syntax {syntax}/{om} must be binary.");
        Check(WindowsDirectoryClient.ReadValue("Grüße"u8.ToArray(), binary: false) == new DirectoryValue.Text("Grüße"), "UTF-8 text was not decoded.");
        Check(WindowsDirectoryClient.ReadValue([0x01, 0xff], binary: true) is DirectoryValue.Binary { Value: [0x01, 0xff] }, "Binary bytes changed.");
        try { _ = WindowsDirectoryClient.ReadValue([0xff, 0xfe], binary: false); throw new InvalidOperationException("Invalid UTF-8 text accepted."); }
        catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.InvalidData, "Invalid UTF-8 text must be InvalidData."); }
    }

    [Test]
    public void DirectoryTextReadsAreTyped()
    {
        ImmutableArray<DirectoryValue> values = [new DirectoryValue.Text("CN=User"), new DirectoryValue.Binary([.. "HTTP/host"u8])];
        Check(ActiveDirectoryClient.ReadText(values).SequenceEqual(["CN=User", "HTTP/host"]), "Directory text values were not decoded.");
        try { _ = ActiveDirectoryClient.ReadText([new DirectoryValue.Binary([0xff, 0xfe])]); throw new InvalidOperationException("Invalid UTF-8 accepted."); }
        catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.InvalidData, "Invalid UTF-8 must be InvalidData."); }
    }

    // 0.10 typed models (I-013).
    [Test]
    public void TaskBoundaryKindSelectsTheSchedulerFormat()
    {
        Check(TaskXml.Boundary(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)) == "2030-01-02T03:04:05", "A wall-clock boundary must have no offset.");
        Check(TaskXml.Boundary(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)) == "2030-01-02T03:04:05Z", "A UTC boundary must end in Z.");
        var local = new DateTime(2030, 7, 2, 3, 4, 5, DateTimeKind.Local);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var expected = "2030-07-02T03:04:05" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh':'mm", System.Globalization.CultureInfo.InvariantCulture);
        Check(TaskXml.Boundary(local) == expected, "A local boundary must carry this computer's offset: " + TaskXml.Boundary(local));
        try { _ = TaskXml.Boundary(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMilliseconds(1)); throw new InvalidOperationException("A fractional second was accepted."); }
        catch (ArgumentException) { }
        var xml = TaskXml.Create(new(new("SYSTEM", TaskLogonType.ServiceAccount), [new(@"C:\Windows\System32\cmd.exe")],
            [new TimeTaskTrigger { StartBoundary = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc), EndBoundary = new DateTime(2030, 1, 2, 12, 0, 0) }]));
        Check(xml.Contains("<StartBoundary>2030-01-01T12:00:00Z</StartBoundary>", StringComparison.Ordinal)
            && xml.Contains("<EndBoundary>2030-01-02T12:00:00</EndBoundary>", StringComparison.Ordinal), "Task XML boundaries changed.");
    }

    [Test]
    public void FirewallProtocolIsAnOpenEnum()
    {
        var rule = new FirewallRuleSpecification("RunicTest", FirewallDirection.Outbound, FirewallAction.Block);
        Check(rule.Protocol == FirewallProtocol.Any && (int)FirewallProtocol.Any == 256, "The default protocol must be Any (256).");
        Check((int)FirewallProtocol.IcmpV4 == 1 && (int)FirewallProtocol.Tcp == 6 && (int)FirewallProtocol.Udp == 17 && (int)FirewallProtocol.IcmpV6 == 58,
            "Protocol values must be the IANA numbers.");
        FirewallContract.Validate(rule with { Protocol = (FirewallProtocol)47 });
        FirewallContract.Validate(rule with { Protocol = FirewallProtocol.Tcp, LocalPorts = "80" });
        FirewallContract.Validate(rule with { Protocol = FirewallProtocol.IcmpV6, IcmpTypesAndCodes = "128:*" });
        foreach (var invalid in new[] { rule with { Protocol = (FirewallProtocol)257 }, rule with { Protocol = (FirewallProtocol)(-1) },
            rule with { Protocol = (FirewallProtocol)47, LocalPorts = "80" }, rule with { Protocol = FirewallProtocol.Tcp, IcmpTypesAndCodes = "8:*" } })
        {
            try { FirewallContract.Validate(invalid); throw new InvalidOperationException("Invalid protocol settings accepted: " + invalid.Protocol); }
            catch (ArgumentException) { }
        }
    }

    // sdk#740: local-store writes under an overriding policy are an explicit opt-in; the state keeps the native numbers.
    [Test]
    public void FirewallLocalPolicyStateIsNative()
    {
        Check(!new FirewallClientOptions().WriteWhenLocalPolicyOverridden, "Writes under an overriding policy must be opt-in.");
        Check((int)FirewallLocalPolicyState.Effective == 0 && (int)FirewallLocalPolicyState.GroupPolicyOverride == 1
            && (int)FirewallLocalPolicyState.InboundBlocked == 2, "NET_FW_MODIFY_STATE values changed.");
    }

    // SEC-14: an update must not produce an inbound allow rule that create would refuse.
    [Test]
    public void FirewallUpdateCannotCreateAnUnscopedInboundAllow()
    {
        var blocked = new FirewallRuleSpecification("RunicTest", FirewallDirection.Inbound, FirewallAction.Block)
        { ApplicationPath = @"C:\App\app.exe", Protocol = FirewallProtocol.Tcp, LocalPorts = "49199" };
        var scoped = blocked with { Action = FirewallAction.Allow };
        var unscoped = new FirewallRuleSpecification("RunicTest", FirewallDirection.Inbound, FirewallAction.Allow);
        foreach (var (existing, next) in new[]
        {
            (blocked, blocked with { Action = FirewallAction.Allow, ApplicationPath = "", LocalPorts = "" }),
            (scoped, scoped with { ApplicationPath = "", LocalPorts = "*" }),
            (scoped, scoped with { ApplicationPath = "", Protocol = FirewallProtocol.Any, LocalPorts = "" }),
            (scoped, scoped with { ApplicationPath = "", LocalPorts = "", ServiceName = "*" }),
            (blocked with { Direction = FirewallDirection.Outbound, Action = FirewallAction.Allow, ApplicationPath = "", LocalPorts = "" },
                blocked with { Action = FirewallAction.Allow, ApplicationPath = "", LocalPorts = "" })
        })
        {
            try { FirewallContract.RequireInboundAllowScope(existing, next); throw new InvalidOperationException("Unscoped inbound allow update accepted."); }
            catch (ArgumentException) { }
        }
        FirewallContract.RequireInboundAllowScope(blocked, blocked with { LocalPorts = "49200" });
        FirewallContract.RequireInboundAllowScope(scoped, scoped with { ApplicationPath = "" });
        FirewallContract.RequireInboundAllowScope(scoped, scoped with { Protocol = FirewallProtocol.IcmpV4, LocalPorts = "", ApplicationPath = "" });
        FirewallContract.RequireInboundAllowScope(blocked, blocked with { ApplicationPath = "", LocalPorts = "" });
        FirewallContract.RequireInboundAllowScope(blocked, blocked with { Direction = FirewallDirection.Outbound, Action = FirewallAction.Allow, ApplicationPath = "", LocalPorts = "" });
        // A rule that already admits all inbound traffic can still be disabled, narrowed or changed.
        FirewallContract.RequireInboundAllowScope(unscoped, unscoped with { Enabled = false });
        FirewallContract.RequireInboundAllowScope(unscoped, unscoped with { Description = "changed" });
    }

    // SEC-30: a remote WMI connection names the server's Kerberos principal; a local one cannot carry an authority.
    [Test]
    public void RemoteWindowsManagementUsesTheServersKerberosPrincipal()
    {
        foreach (var local in new[] { ".", "localhost", "LOCALHOST", Environment.MachineName, Environment.MachineName.ToLowerInvariant(), "127.0.0.1", "::1", "[::1]" })
            Check(WmiConnection.IsLocalMachine(local), local + " must be local.");
        foreach (var remote in new[] { "runic-dc1.runic.test", "192.0.2.10", "runic-remote-" + Guid.NewGuid().ToString("N")[..8] })
            Check(!WmiConnection.IsLocalMachine(remote), remote + " must be remote.");
        Check(WmiConnection.RemotePrincipal("runic-dc1.runic.test") == "host/runic-dc1.runic.test", "Unexpected remote principal.");
        Check(WmiConnection.RemotePrincipal("runic-dc1.runic.test.") == "host/runic-dc1.runic.test", "A trailing root dot must be dropped.");
        Check(WmiConnection.RemotePrincipal("RUNIC-DC1") == "host/RUNIC-DC1", "A NetBIOS name must be kept.");
        foreach (var address in new[] { "192.0.2.10", "2001:db8::1", "[2001:db8::1]" })
        {
            try { _ = WmiConnection.RemotePrincipal(address); throw new InvalidOperationException("An IP address has no Kerberos principal: " + address); }
            catch (ArgumentException) { }
        }
    }

    // I-054: connection reuse is opt-in, and a directory client can be disposed.
    [Test]
    public void DirectoryConnectionReuseIsOptIn()
    {
        Check(!new DirectoryConnectionOptions("dc").ReuseConnection, "Directory clients must not reuse connections by default.");
        Check(typeof(IDisposable).IsAssignableFrom(typeof(WindowsDirectoryClient)), "WindowsDirectoryClient must be disposable.");
    }

    // sdk#739: the executable of a service command line, parsed as the SCM (CreateProcess) does.
    [Test]
    public void ServiceExecutablePathFollowsTheServiceControlManager()
    {
        foreach (var (commandLine, expected) in new[]
        {
            (@"""C:\Program Files\App\svc.exe"" -k ""a b""", @"C:\Program Files\App\svc.exe"),
            (@"  ""C:\Program Files\App\svc.exe""--flag", @"C:\Program Files\App\svc.exe"),
            (@"""C:\Unterminated\svc.exe", @"C:\Unterminated\svc.exe"),
            (@"C:\Windows\system32\svchost.exe -k netsvcs -p", @"C:\Windows\system32\svchost.exe"),
            (@"C:\Program Files\App\svc.exe -k", @"C:\Program Files\App\svc.exe"),
            (@"C:\Program Files\App Dir\svc.EXE", @"C:\Program Files\App Dir\svc.EXE"),
            ("C:\\Tools\\agent\t--run", @"C:\Tools\agent"),
            (@"%SystemRoot%\System32\svchost.exe -k LocalService", @"%SystemRoot%\System32\svchost.exe"),
            (@"\??\C:\Windows\system32\drivers\x.sys", @"C:\Windows\system32\drivers\x.sys"),
            ("", ""), ("   ", "")
        })
        {
            var actual = ServiceCommandLine.ExecutablePath(commandLine, null);
            Check(actual == expected, $"{commandLine} parsed as {actual}, not {expected}.");
        }
        // With a file system, the first existing prefix wins, as when the SCM starts the service.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program.exe", @"C:\Program Files\App Dir\svc.exe", @"C:\Tools\agent.exe" };
        Check(ServiceCommandLine.ExecutablePath(@"C:\Program Files\App\svc.exe -k", files.Contains) == @"C:\Program.exe", "An existing shorter prefix must win.");
        files.Remove(@"C:\Program.exe");
        Check(ServiceCommandLine.ExecutablePath(@"C:\Program Files\App Dir\svc.exe -k", files.Contains) == @"C:\Program Files\App Dir\svc.exe", "The existing executable must win.");
        Check(ServiceCommandLine.ExecutablePath(@"C:\Tools\agent --run", files.Contains) == @"C:\Tools\agent.exe", ".exe must be added to a prefix without extension.");
        Check(ServiceCommandLine.ExecutablePath(@"C:\Missing Dir\svc.exe -k", files.Contains) == @"C:\Missing Dir\svc.exe", "Without an existing prefix, the .exe rule applies.");
        Check(ServiceCommandLine.ExecutablePath(@"""C:\Program Files\App Dir\svc.exe""", _ => throw new InvalidOperationException("A quoted path was probed.")) == @"C:\Program Files\App Dir\svc.exe",
            "A quoted path must not be probed.");
        Check(new ServiceSnapshot("s", "s", "", "", [], ServiceStartMode.Manual, 0x10, 1, "", "", false, new(null, []), new(ServiceState.Stopped, 0, 0, 0, 0, 0, TimeSpan.Zero)).ExecutablePath == "",
            "ExecutablePath must default to empty.");
    }

    [Test]
    public void ServiceStateConflictsAreConflicts()
    {
        // ERROR_DEPENDENT_SERVICES_RUNNING, _SERVICE_ALREADY_RUNNING, _SERVICE_CANNOT_ACCEPT_CTRL, _SERVICE_NOT_ACTIVE.
        foreach (var code in new[] { 1051, 1056, 1061, 1062 })
            Check(NativeError.Categorize(code) == AdministrationErrorCategory.Conflict, $"{code} must be Conflict.");
    }

    [Test]
    public void AcceptedControlsAreNativeFlags()
    {
        Check(typeof(ServiceAcceptedControls).IsDefined(typeof(FlagsAttribute), false), "ServiceAcceptedControls must be a flags enum.");
        // SERVICE_ACCEPT_STOP, _PAUSE_CONTINUE, _SHUTDOWN, _PRESHUTDOWN, _SYSTEMLOWRESOURCES.
        Check((int)ServiceAcceptedControls.Stop == 0x1 && (int)ServiceAcceptedControls.PauseContinue == 0x2 && (int)ServiceAcceptedControls.Shutdown == 0x4
            && (int)ServiceAcceptedControls.Preshutdown == 0x100 && (int)ServiceAcceptedControls.SystemLowResources == 0x4000, "SERVICE_ACCEPT_* values changed.");
        var status = new global::Windows.Win32.System.Services.SERVICE_STATUS_PROCESS { dwCurrentState = (global::Windows.Win32.System.Services.SERVICE_STATUS_CURRENT_STATE)4, dwControlsAccepted = 0x1005 }.Snapshot();
        Check(status.AcceptedControls == (ServiceAcceptedControls.Stop | ServiceAcceptedControls.Shutdown | (ServiceAcceptedControls)0x1000),
            "Unnamed native bits must pass through.");
    }
}
