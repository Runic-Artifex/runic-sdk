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
var service = services.Find("EventLog");
if (service is not null)
{
    Console.WriteLine(service.BinaryCommandLine);
    Console.WriteLine(service.Status);
    if (service.Status.ProcessId != 0)
        Console.WriteLine(new WindowsProcessClient().Find(service.Status.ProcessId));
}
~~~

Create accepts ServiceSpecification, Update accepts ServiceUpdate. On a disposable
fixture, provide a real disposable Windows service executable, then exercise
Start/Stop/Pause/Continue only if that service supports them. WaitForStateAsync
uses an explicit timeout. A normal console executable is not a valid service
fixture. Deletion may mark a service for later removal while handles remain open.

## Scheduled tasks

~~~csharp
using Runic.Platform.Administration.Windows.Tasks;

var tasks = new WindowsTaskSchedulerClient();
var definition = new ScheduledTaskSpecification(
    new("SYSTEM", TaskLogonType.ServiceAccount, true),
    [new(@"C:\Windows\System32\cmd.exe", "/c exit 0")],
    [new DailyTaskTrigger { StartBoundary = "2030-01-01T12:00:00" }]);

await tasks.ValidateAsync(definition); // Does not register or execute it.
foreach (var task in await tasks.EnumerateAsync())
    Console.WriteLine($"{task.Path}: {task.State}, last result {task.LastTaskResult}");
~~~

On the fixture, CreateAsync registers the definition; UpdateAsync edits supplied
fields without delete-before-update. RunAsync returns an instance and scheduler
state, not an assertion that the command succeeded. FindAsync returns last-run
information and complete XML. RegisterXmlAsync opts into replacement explicitly.
Time, daily, weekly, monthly, boot, logon, idle, registration, event and session
triggers are supported.

## Firewall

~~~csharp
using Runic.Platform.Administration.Windows.Firewall;

var firewall = new WindowsFirewallClient();
foreach (var profile in await firewall.GetProfilesAsync()) Console.WriteLine(profile);
var existing = await firewall.FindAsync("A fixture-owned unique rule name");
if (existing is not null) Console.WriteLine(existing.Configuration);
~~~

Fixture writes use FirewallRuleSpecification and FirewallRuleUpdate, and the
identity from the returned snapshot. An ambiguous name is a conflict. For a
protocol change, explicitly clear incompatible ports/ICMP settings; null means
preserve, not clear. Local Group Policy restrictions fail rather than claiming
a rule became effective.

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
CreateOrganizationalUnitAsync provide curated creation. Directory ModifyAsync,
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
Console.WriteLine(domains.GetMembership());
// Explicit discovery on a domain fixture:
// Console.WriteLine(domains.DiscoverController("example.test"));
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
foreach (var share in shares.Enumerate()) Console.WriteLine(share);
var snapshot = shares.Find("FixtureShare");
if (snapshot is not null)
    Console.WriteLine($"{snapshot.Path}: stored security: {snapshot.SecurityDescriptor?.Length.ToString() ?? "absent"}");
~~~

On a disposable server, Create accepts ShareSpecification. Update can replace
description, maximum uses or the complete self-relative security descriptor.
On reads, a null SecurityDescriptor means Windows returned no stored descriptor;
it is not an empty (deny-all) DACL and does not describe filesystem permissions.
On updates, null preserves each field. Decode present descriptors with RawSecurityDescriptor when needed; retain
unknown ACEs, ordering and inheritance when editing. Delete removes the share,
not its directory or files.

## DNS

~~~csharp
using Runic.Platform.Administration.Windows.Dns;

var dns = new WindowsDnsClient("dns1.example.test");
foreach (var zone in await dns.EnumerateZonesAsync()) Console.WriteLine(zone);
foreach (var record in await dns.EnumerateRecordsAsync("example.test"))
    Console.WriteLine(record);
~~~

Fixture writes use DnsRecordKey plus DnsRecordSpecification. The key includes
zone, owner and typed record data. TTL is separate. Find/Update/Delete target a
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
the library does not choose one. Inspect result messages as well as the result.
Link and permission methods act on explicit scopes/trustees. Inherited/custom
permissions remain visible; removal of a trustee is explicit. Missing GPMC is an
Unavailable prerequisite failure, not an empty domain.
