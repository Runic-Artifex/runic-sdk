# Console examples

Each snippet is a .NET 10 console Program.cs using only the local NuGet package.
Add an OperatingSystem.IsWindows() guard in a cross-platform caller. Administrative
examples are for an explicitly provisioned disposable fixture, using delegated
credentials/permissions. None raises privileges automatically.

## Shortcuts

~~~csharp
using Runic.Platform.Administration.Windows.Shortcuts;

var links = new WindowsShellLinkClient();
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lnk");
try
{
    await links.CreateAsync(path, new(@"C:\Windows\System32\notepad.exe")
    {
        Arguments = "\"C:\\Documents\\Grüße.txt\"",
        Description = "Unicode example"
    });
    await links.UpdateAsync(path, new() { Description = "Revised" });
    Console.WriteLine(await links.FindAsync(path)); // No resolution.
}
finally { File.Delete(path); }
~~~

ResolveAsync is separate, uses no UI and does not update the source file.
Shell/provider resolution has a timeout request, not a guaranteed hard deadline.

## Services and processes

~~~csharp
using Runic.Platform.Administration.Windows.Services;
using Runic.Platform.Administration.Windows.Processes;

var services = new WindowsServiceClient(); // Or explicit remote machine.
var service = await services.FindAsync("EventLog");
if (service is not null)
{
    Console.WriteLine(service.BinaryCommandLine);
    Console.WriteLine(service.Status);
    if (service.Status.ProcessId != 0)
        Console.WriteLine(new WindowsProcessClient().Find(service.Status.ProcessId));
}
~~~

CreateAsync accepts ServiceSpecification, UpdateAsync accepts ServiceUpdate. On a disposable
fixture, provide a real disposable Windows service executable, then exercise
StartAsync/StopAsync/PauseAsync/ContinueAsync only if that service supports them. WaitForStateAsync
uses an explicit timeout. A missing service is reported as `AdministrationErrorCategory.NotFound`. A normal console executable is not a valid service
fixture. Deletion may mark a service for later removal while handles remain open.

## Scheduled tasks

~~~csharp
using Runic.Platform.Administration.Windows.Tasks;

var tasks = new WindowsTaskSchedulerClient();
var definition = new ScheduledTaskSpecification(
    new("SYSTEM", TaskLogonType.ServiceAccount, true),
    [new(@"C:\Windows\System32\cmd.exe", "/c exit 0")],
    [new DailyTaskTrigger { StartBoundary = new DateTime(2030, 1, 1, 12, 0, 0) }]);

await tasks.ValidateAsync(definition); // Does not register or execute it.
foreach (var task in await tasks.EnumerateAsync())
    Console.WriteLine($"{task.Path}: {task.State}, last result {task.LastTaskResult}");
~~~

On the fixture, CreateAsync registers the definition; UpdateAsync edits supplied
fields without delete-before-update. RunAsync returns an instance and scheduler
state, not an assertion that the command succeeded. FindAsync returns last-run
information and complete XML. RegisterXmlAsync opts into replacement explicitly.
Time, daily, weekly, monthly, boot, logon, idle, registration, event and session
triggers are supported. A trigger boundary's `DateTime.Kind` selects its meaning:
`Unspecified` is a wall-clock time of the target machine (Task Scheduler's
default), `Utc` and `Local` are points in time written with an offset ("synchronize
across time zones"). Boundaries have whole seconds.

## Firewall

~~~csharp
using Runic.Platform.Administration.Windows.Firewall;

var firewall = new WindowsFirewallClient();
foreach (var profile in await firewall.GetProfilesAsync()) Console.WriteLine(profile);
var existing = await firewall.FindAsync("A fixture-owned unique rule name");
if (existing is not null) Console.WriteLine(existing.Configuration);

// An installer that created a rule several times over the years, under a Group Policy that may override local rules:
var installer = new WindowsFirewallClient(new FirewallClientOptions { WriteWhenLocalPolicyOverridden = true });
var deleted = await installer.DeleteAllAsync("Contoso Agent");
var state = await installer.GetLocalPolicyStateAsync();
if (state != FirewallLocalPolicyState.Effective) Console.WriteLine($"Local firewall rules do not apply now ({state}).");
~~~

Fixture writes use FirewallRuleSpecification and FirewallRuleUpdate, and the
identity from the returned snapshot. `Protocol` is a `FirewallProtocol`: `Tcp` and
`Udp` allow ports, `IcmpV4` and `IcmpV6` allow ICMP types, `Any` is the default, and
other IANA protocol numbers are cast, for example `(FirewallProtocol)47`. An ambiguous name is a conflict. For a
protocol change, explicitly clear incompatible ports/ICMP settings; null means
preserve, not clear. Local Group Policy restrictions fail rather than claiming
a rule became effective, unless the client opts in with
`FirewallClientOptions.WriteWhenLocalPolicyOverridden`.

## LDAP and Active Directory

~~~csharp
using Runic.Platform.Administration.Windows.DirectoryServices;

var directory = new WindowsDirectoryClient(new("dc1.example.test")
{
    Transport = DirectoryTransport.Tls
}); // Current Windows identity; optional NetworkCredential supplied separately.
var root = await directory.ReadRootDseAsync(["defaultNamingContext"]);
var objects = await directory.SearchAsync(new(
    "DC=example,DC=test", "(objectClass=organizationalUnit)",
    DirectorySearchScope.Subtree, ["distinguishedName", "name"]) { PageSize = 100 });
foreach (var item in objects) Console.WriteLine(item.DistinguishedName);

var ad = new ActiveDirectoryClient(directory);
var members = await ad.GetMembersAsync("CN=FixtureGroup,OU=Fixture,DC=example,DC=test");
Console.WriteLine(members.Length);
~~~

CreateUserAsync, CreateComputerAsync, CreateGroupAsync and
CreateOrganizationalUnitAsync provide curated creation. Each returns the new
object's `ActiveDirectoryObjectIdentity`: its distinguished name as stored and its
`objectGUID`, which stays the same across renames and moves. If reading the
identity fails after the object was created, the exception's operation is "Read
created directory object" and the object exists. Directory ModifyAsync,
DeleteAsync and MoveAsync provide schema-extensible editing, deletion, rename and
move without an application object model. Use DirectoryNames.EscapeFilterValue
for literal filter values and EscapeRdnValue for RDN values; these are different
escaping rules. Binary attributes remain bytes. Signed/sealed or TLS protection
is required; password APIs reject insufficient protection and never turn off
certificate validation.

## Domain discovery, system information and networks

~~~csharp
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.SystemInformation;
using Runic.Platform.Administration.Windows.Networks;

var domains = new WindowsDomainClient();
var membership = await domains.GetMembershipAsync();
// Name is the flat (NetBIOS) name; DnsDomainName and ForestName are null outside a domain.
Console.WriteLine($"{membership.Name} {membership.DnsDomainName} {membership.ForestName}");
// Explicit discovery on a domain fixture, with DNS names or the controller's NetBIOS name:
// Console.WriteLine(await domains.DiscoverControllerAsync("example.test"));
// Console.WriteLine((await domains.DiscoverControllerAsync("example.test", false, true, DomainControllerNameFormat.Flat)).Name);
var system = new WindowsSystemInformationClient();
Console.WriteLine(await system.GetOperatingSystemNameAsync());
Console.WriteLine(system.GetOperatingSystem());
foreach (var bios in system.GetBiosInformation()) Console.WriteLine(bios);
foreach (var network in await new WindowsNetworkClient().EnumerateAsync())
    Console.WriteLine(network);
~~~

## SMB shares

~~~csharp
using Runic.Platform.Administration.Windows.Shares;

var shares = new WindowsShareClient("fileserver.example.test");
foreach (var share in await shares.EnumerateAsync()) Console.WriteLine(share);
var snapshot = await shares.FindAsync("FixtureShare");
if (snapshot is not null)
    Console.WriteLine($"{snapshot.Path}: stored security: {snapshot.SecurityDescriptor?.Sddl ?? "absent"}");
~~~

On a disposable server, CreateAsync accepts ShareSpecification, which requires an
explicit share security descriptor, for example
`WindowsSecurityDescriptor.FromSddl("O:BAG:BAD:(A;;FA;;;BA)(A;;FR;;;BU)")`.
UpdateAsync can replace description, maximum uses or the complete security
descriptor. On reads, a null SecurityDescriptor means Windows returned no stored
descriptor; it is not an empty (deny-all) DACL and does not describe filesystem
permissions. On updates, null preserves each field. `WindowsSecurityDescriptor`
is immutable and compares by its binary form. To edit one, call
`ToRawSecurityDescriptor()`, change the copy, and pass
`WindowsSecurityDescriptor.From(raw)`; retain unknown ACEs, ordering and
inheritance when editing. Delete removes the share,
not its directory or files (DeleteAsync).

## DNS

~~~csharp
using Runic.Platform.Administration.Windows.Dns;

var dns = new WindowsDnsClient("dns1.example.test");
foreach (var zone in await dns.EnumerateZonesAsync()) Console.WriteLine(zone);
foreach (var record in await dns.EnumerateRecordsAsync("example.test"))
    Console.WriteLine(record);
~~~

Fixture writes use DnsRecordKey plus DnsRecordSpecification. The key includes
zone, owner and typed record data; A and AAAA data are `IPAddress` values, so
equivalent spellings of one address are the same key. TTL is a separate
`TimeSpan` in whole seconds; `TimeToLive = null` on a specification lets the
server choose it.

~~~csharp
var key = new DnsRecordKey("example.test", "host.example.test",
    new DnsRecordData.A(System.Net.IPAddress.Parse("198.51.100.10")));
await dns.CreateAsync(new(key) { TimeToLive = TimeSpan.FromMinutes(5) });
await dns.UpdateAsync(key, new() { TimeToLive = TimeSpan.FromMinutes(10) });
~~~
 Find/Update/Delete target a
single record, preserving other records at that owner. Unsupported native types
are exposed and rejected for typed writes. The DNS role and remote WMI access
are prerequisites.

## Group Policy

~~~csharp
using Runic.Platform.Administration.Windows.GroupPolicy;

var policies = new WindowsGroupPolicyClient("example.test", "dc1.example.test");
foreach (var gpo in await policies.EnumerateAsync()) Console.WriteLine(gpo);
~~~

On the domain fixture, use GUID identities with BackupAsync, ImportAsync,
RestoreAsync and GenerateReportAsync. Select the exact backup GUID/directory;
the library does not choose one. Inspect the outcome's messages as well as its value.
Link and permission methods act on explicit scopes/trustees. Inherited/custom
permissions remain visible; removal of a trustee is explicit. Missing GPMC is a
NotInstalled prerequisite failure, not an empty domain.
