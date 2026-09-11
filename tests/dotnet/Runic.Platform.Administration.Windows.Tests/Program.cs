using Runic.Platform.Administration.Windows.Internal.Backends;
using System.Runtime.Versioning;
using Runic.Platform.Administration.Windows.GroupPolicy;
using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.SystemInformation;
using Runic.Platform.Administration.Windows.Networks;
using Runic.Platform.Administration.Windows.Shares;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Tasks;
using Runic.Platform.Administration.Windows;
using Runic.Platform.Administration.Windows.Processes;
using Runic.Platform.Administration.Windows.Services;
using Runic.Platform.Administration.Windows.Shortcuts;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SKIP Windows native administration tests.");
    return;
}
await NativeTests.RunAsync();

[SupportedOSPlatform("windows")]
internal static class NativeTests
{
    internal static async Task RunAsync()
    {
        CheckDuplicateShareError();
        await CheckShortcutsAsync();
        CheckProcesses();
        await CheckServicesAsync();
        await CheckTaskInspectionAsync();
        await CheckFirewallInspectionAsync();
        await CheckPilotBackendsAsync();
        CheckLdapTransport();
        await CheckInventoryAsync();
        await CheckNativeWmiAsync();
        await CheckTaskDefinitionsAsync();
        CheckLinkOrder();
        await CheckGpmcPrerequisiteAsync();
        await CheckBackupEnumerationAsync();
        Console.WriteLine("PASS Windows administration: native shortcuts, process snapshots and read-only service queries.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckPilotBackendsAsync()
    {
        // These reads exercise both native projections against the same Windows state, without mutation.
        var handwritten = AdministrationBackends.Firewall("handwritten");
        var generated = AdministrationBackends.Firewall("cswin32");
        Check((await handwritten.GetProfilesAsync()).SequenceEqual(await generated.GetProfilesAsync()),
            "Firewall backends disagree on effective profiles.");
        var expectedRules = (await handwritten.EnumerateAsync()).GroupBy(rule =>
            (rule.Configuration with { Interfaces = default }, string.Join('\0', rule.Configuration.Interfaces)))
            .ToDictionary(group => group.Key, group => group.Count());
        var actualRules = (await generated.EnumerateAsync()).GroupBy(rule =>
            (rule.Configuration with { Interfaces = default }, string.Join('\0', rule.Configuration.Interfaces)))
            .ToDictionary(group => group.Key, group => group.Count());
        Check(expectedRules.Count == actualRules.Count && expectedRules.All(pair => actualRules.GetValueOrDefault(pair.Key) == pair.Value),
            "Firewall backends disagree on rule data or duplicates.");
        var missing = "RunicMissing-" + Guid.NewGuid().ToString("N");
        foreach (var backend in new[] { "handwritten", "cswin32" })
        {
            Check(await AdministrationBackends.Firewall(backend).FindAsync(missing) is null, "Missing firewall rule must remain absent.");
            Check(AdministrationBackends.Shares(backend).Find(missing) is null, "Missing share must remain absent.");
            try
            {
                await AdministrationBackends.Firewall(backend).CreateAsync(new(missing, FirewallDirection.Inbound, FirewallAction.Block)
                { Protocol = 1, LocalPorts = "80" });
                throw new InvalidOperationException("Invalid firewall specification reached native creation.");
            }
            catch (ArgumentException) { }
        }
        var expectedShares = AdministrationBackends.Shares("handwritten").Enumerate().OrderBy(share => share.Name, StringComparer.Ordinal);
        var actualShares = AdministrationBackends.Shares("cswin32").Enumerate().OrderBy(share => share.Name, StringComparer.Ordinal);
        Check(expectedShares.SequenceEqual(actualShares), "Share backends disagree on native enumeration.");
        Console.WriteLine("PASS Handwritten/CsWin32 parity: firewall profiles/rules, SMB enumeration, missing lookups and validation.");
    }

    private static void CheckDuplicateShareError()
    {
        var error = NativeError.Win32("Create SMB share", 2118); // NERR_DuplicateShare
        Check(error.Category == AdministrationErrorCategory.Conflict, "Duplicate SMB share must be a conflict.");
        Check(error.NativeErrorDomain == NativeErrorDomain.Win32 && error.NativeErrorCode == 2118 &&
            error.Operation == "Create SMB share", "Conflict classification must retain the native error identity.");
    }

    private static async Task CheckShortcutsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RunicAdmin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var client = new WindowsShellLinkClient();
            var path = Path.Combine(folder, "Überprüfung.lnk");
            var target = Path.Combine(folder, "Ziel ä.txt");
            await File.WriteAllTextAsync(target, "fixture");
            Check(await client.FindAsync(path) is null, "Missing shortcut must return null.");
            var specification = new ShellLinkSpecification(target)
            {
                Arguments = "--name \"Schüler ä\"",
                WorkingDirectory = folder,
                Description = "Beschreibung ä",
                Icon = new(target, -12),
                ShowState = ShellLinkShowState.Maximized,
                Hotkey = 0x0241
            };
            await client.CreateAsync(path, specification);
            var before = await client.FindAsync(path) ?? throw new InvalidOperationException("Created link missing.");
            Check(before.TargetPath == target && before.Arguments == specification.Arguments, "Target/arguments did not roundtrip.");
            Check(before.Description == specification.Description && before.WorkingDirectory == folder, "Unicode metadata did not roundtrip.");
            Check(before.Icon == specification.Icon && before.ShowState == 3 && before.Hotkey == specification.Hotkey, "Icon/show state/hotkey did not roundtrip.");
            try { await client.CreateAsync(path, new(target)); throw new InvalidOperationException("Duplicate shortcut was overwritten."); }
            catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.Conflict, "Duplicate shortcut was not classified as conflict."); }
            await client.UpdateAsync(path, new() { Description = "Changed" });
            var updated = await client.FindAsync(path);
            Check(updated == before with { Description = "Changed" }, "Selected-field update lost unrelated metadata.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await client.UpdateAsync(path, new() { Description = "Should not be written" }, cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Check(await client.FindAsync(path) == updated, "Cancelled update changed the file.");
            var originalBytes = await File.ReadAllBytesAsync(path);
            var resolved = await client.ResolveAsync(path, TimeSpan.FromSeconds(1));
            Check(resolved.TargetPath == target, "Explicit resolution failed.");
            var resolvedBytes = await File.ReadAllBytesAsync(path); Check(originalBytes.SequenceEqual(resolvedBytes), "Resolution wrote the source link.");
            await client.CreateAsync(path, specification with { Arguments = "replacement" }, true);
            Check((await client.FindAsync(path))?.Arguments == "replacement", "Explicit replacement failed.");
            var unc = Path.Combine(folder, "unc.lnk");
            await client.CreateAsync(unc, new(@"\\runic-invalid-host\share\target.txt"));
            Check((await client.FindAsync(unc))?.TargetPath == @"\\runic-invalid-host\share\target.txt", "UNC target did not roundtrip.");
            await File.WriteAllTextAsync(path, "invalid shell link");
            try { _ = await client.FindAsync(path); throw new InvalidOperationException("Malformed shortcut accepted."); }
            catch (WindowsAdministrationException) { }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task CheckTaskInspectionAsync()
    {
        var client = new WindowsTaskSchedulerClient();
        var folders = await client.EnumerateFoldersAsync();
        Check(folders.Length > 0, "Task Scheduler folders unavailable on fixture.");
        _ = await client.EnumerateAsync();
        Check(await client.FindAsync("\\RunicMissing-" + Guid.NewGuid().ToString("N")) is null, "Missing task must return null.");
        Console.WriteLine("PASS Native Task Scheduler connection, folder/task enumeration and missing lookup.");
    }
    private static async Task CheckFirewallInspectionAsync()
    {
        var client = new WindowsFirewallClient();
        Check((await client.GetProfilesAsync()).Length == 3, "Expected all firewall profiles.");
        _ = await client.EnumerateAsync();
        Check(await client.FindAsync("RunicMissing-" + Guid.NewGuid().ToString("N")) is null, "Missing firewall rule must return null.");
        Console.WriteLine("PASS Native firewall profile and rule enumeration; missing rule lookup.");
    }
    private static void CheckLdapTransport()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var connection = new LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", port));
        connection.Timeout = TimeSpan.FromSeconds(1);
        connection.AuthType = AuthType.Negotiate;
        try { connection.Bind(); throw new InvalidOperationException("Unexpected LDAP endpoint on temporary port."); }
        catch (LdapException error) { Check(error.ErrorCode is 81 or 82 or 85 or 91, "Unexpected LDAP failure: " + error.ErrorCode); }
        Console.WriteLine("PASS LDAP native transport initialization and unavailable-endpoint failure; domain operations require a fixture.");
    }
    private static async Task CheckInventoryAsync()
    {
        var system = new WindowsSystemInformationClient();
        var nativeVersion = system.GetOperatingSystem().Version; var managedVersion = Environment.OSVersion.Version;
        Check(nativeVersion.Major == managedVersion.Major && nativeVersion.Minor == managedVersion.Minor && nativeVersion.Build == managedVersion.Build, $"Native OS version mismatch: {nativeVersion} / {managedVersion}.");
        Check(system.GetBiosInformation().Length > 0, "BIOS unavailable on fixture.");
        _ = system.GetDomainMembership();
        _ = await new WindowsNetworkClient().EnumerateAsync();
        var shares = new WindowsShareClient();
        _ = shares.Enumerate();
        Check(shares.Find("RunicMissing-" + Guid.NewGuid().ToString("N")) is null, "Missing share must return null.");
        Console.WriteLine("PASS Native OS/BIOS, domain membership, networks and SMB share inspection.");
    }
    private static async Task CheckNativeWmiAsync()
    {
        await ComApartment.RunAsync(() =>
        {
            using var connection = new WmiConnection(".", @"root\cimv2", null, TimeSpan.FromSeconds(5));
            var rows = connection.Query("SELECT * FROM Win32_OperatingSystem", ["__PATH", "Caption", "Version"], default);
            Check(rows.Length == 1 && rows[0]["Caption"] is string && rows[0]["Version"] is string, "WMI OS inspection incomplete.");
            var path = (string)rows[0]["__PATH"]!;
            Check(connection.Read(path, ["Version"])["Version"] as string == rows[0]["Version"] as string, "WMI query/object read mismatch.");
            connection.Invoke($"Win32_Process.Handle=\"{Environment.ProcessId}\"", "GetOwner", new Dictionary<string, object>());
            return true;
        }, default);
        Check(!string.IsNullOrWhiteSpace(await new WindowsSystemInformationClient().GetOperatingSystemNameAsync()), "OS display name unavailable.");
        Check(DirectoryNames.EscapeFilterValue("a*(b)\\") == @"a\2A\28b\29\5C", "LDAP filter escaping mismatch.");
        Check(DirectoryNames.EscapeRdnValue(" x,y ") == @"\ x\,y\ ", "RDN escaping mismatch.");
        Console.WriteLine("PASS Native WMI local query/object read/read-only method and LDAP name escaping.");
    }
    private static async Task CheckTaskDefinitionsAsync()
    {
        var client = new WindowsTaskSchedulerClient();
        ImmutableArray<TaskTrigger> triggers = [
            new TimeTaskTrigger { StartBoundary = "2030-01-01T12:00:00" },
            new DailyTaskTrigger { StartBoundary = "2030-01-01T12:00:00" },
            new WeeklyTaskTrigger([DayOfWeek.Monday]) { StartBoundary = "2030-01-01T12:00:00" },
            new MonthlyTaskTrigger([1, 6], [1, 15], true) { StartBoundary = "2030-01-01T12:00:00" },
            new BootTaskTrigger(), new LogonTaskTrigger(), new IdleTaskTrigger(), new RegistrationTaskTrigger(),
            new EventTaskTrigger("<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[(EventID=1)]]</Select></Query></QueryList>"),
            new SessionStateTaskTrigger(TaskSessionChange.SessionLock)
        ];
        var specification = new ScheduledTaskSpecification(new("SYSTEM", TaskLogonType.ServiceAccount, true),
            [new(@"C:\Windows\System32\cmd.exe", "/c exit 0")], triggers);
        await client.ValidateAsync(specification with { Triggers = [] });
        foreach (var trigger in triggers) { await client.ValidateAsync(specification with { Triggers = [trigger] }); }
        await client.ValidateAsync(specification);
        var xml = TaskXml.Create(specification);
        var patched = TaskXml.Update(xml, new() { Description = "Updated" });
        await client.ValidateXmlAsync(patched);
        var before = System.Xml.Linq.XDocument.Parse(xml);
        var after = System.Xml.Linq.XDocument.Parse(patched);
        Check(System.Xml.Linq.XNode.DeepEquals(before.Root!.Element(TaskXml.Namespace + "Triggers"), after.Root!.Element(TaskXml.Namespace + "Triggers")), "Task patch changed unrelated triggers.");
        Console.WriteLine("PASS Windows validates all typed task trigger definitions and selected-field updates without registration.");
    }

    private static async Task CheckGpmcPrerequisiteAsync()
    {
        await ComApartment.RunAsync(() =>
        {
            try
            {
                using var gpm = ComObject.Create(new("f5694708-88fe-4b35-babf-e56162d5fbc8"), new("f5fae809-3bd6-4da9-a65e-17665b41d763"));
                using var criteria = Automation.GetObject(gpm, 12, "Create GPMC search criteria");
                Console.WriteLine("PASS GPMC activation and search criteria; domain operations still require a fixture.");
            }
            catch (WindowsAdministrationException error) when (error.NativeErrorCode == unchecked((int)0x80040154))
            {
                Check(error.Category == AdministrationErrorCategory.Unavailable, "Missing GPMC was not classified as a prerequisite failure.");
                Console.WriteLine("PASS Missing GPMC is an identifiable Unavailable prerequisite; domain operations not executed.");
            }
            return true;
        }, default);
    }


    private static async Task CheckBackupEnumerationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RunicBackupEnumeration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            try { await WindowsGroupPolicyClient.EnumerateBackupsAsync("relative"); throw new InvalidOperationException("Relative backup path accepted."); }
            catch (ArgumentException) { }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await WindowsGroupPolicyClient.EnumerateBackupsAsync(directory, cancellation.Token); throw new InvalidOperationException("Canceled enumeration succeeded."); }
            catch (OperationCanceledException) { }
            try
            {
                for (var i = 0; i < 3; i++)
                    Check((await WindowsGroupPolicyClient.EnumerateBackupsAsync(directory)).IsEmpty, "Empty backup directory returned data.");
                Console.WriteLine("PASS GPMC empty backup enumeration without domain connection.");
            }
            catch (WindowsAdministrationException error) when (error.NativeErrorCode == unchecked((int)0x80040154))
            {
                Check(error.Category == AdministrationErrorCategory.Unavailable, "Missing GPMC must remain a prerequisite failure.");
                Console.WriteLine("PASS Backup enumeration prerequisite and cancellation; native backup fixture unavailable.");
            }
        }
        finally { Directory.Delete(directory); }
    }

    private static void CheckLinkOrder()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var low = $"[LDAP://CN={first:B},CN=Policies,CN=System,DC=example,DC=test;2]";
        var high = $"[LDAP://CN={second:B},CN=Policies,CN=System,DC=example,DC=test;1]";
        Check(WindowsGroupPolicyClient.ReorderLinks(low + high, [second, first], [first, second]) == high + low, "GPO reordering lost direction or flags.");
        try { _ = WindowsGroupPolicyClient.ReorderLinks(low + high, [second, first], [first]); throw new InvalidOperationException("Incomplete order accepted."); }
        catch (WindowsAdministrationException error) { Check(error.Category == AdministrationErrorCategory.Conflict, "Expected link-order conflict."); }
        Console.WriteLine("PASS GPO link ordering preserves raw flags and rejects incomplete replacements; no directory write performed.");
    }
    private static void CheckProcesses()
    {
        var client = new WindowsProcessClient();
        var snapshot = client.Enumerate();
        Check(snapshot.Any(row => row.ProcessId == (uint)Environment.ProcessId), "Current process missing from snapshot.");
        Check(client.Find(uint.MaxValue) is null, "Missing process must return null.");
        Check(client.GetChildren((uint)Environment.ProcessId).All(row => row.ParentProcessId == (uint)Environment.ProcessId), "Child filter mismatch.");
    }

    private static async Task CheckServicesAsync()
    {
        var client = new WindowsServiceClient();
        var missing = "RunicMissing-" + Guid.NewGuid().ToString("N");
        Check(client.Find(missing) is null && client.FindStatus(missing) is null, "Missing service must return null.");
        var rows = client.Enumerate();
        Check(rows.Length > 0, "No services returned.");
        // EventLog is queried only; this test never changes a service or machine configuration.
        var service = client.Find("EventLog") ?? throw new InvalidOperationException("EventLog unavailable on fixture.");
        Check(service.BinaryCommandLine.Length > 0 && service.AccountName.Length > 0, "Service configuration incomplete.");
        Check(service.Status.State == ServiceState.Running, "EventLog is not running on fixture.");
        Check(new WindowsProcessClient().Find(service.Status.ProcessId) is not null, "Service process ID is not observable.");
        var result = await client.WaitForStateAsync("EventLog", ServiceState.Running, TimeSpan.FromSeconds(1));
        Check(result.State == ServiceState.Running, "Wait failed for already-reached state.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await client.WaitForStateAsync("EventLog", ServiceState.Stopped, TimeSpan.FromSeconds(1), cancellation.Token); throw new InvalidOperationException("Cancelled wait accepted."); }
        catch (OperationCanceledException) { }
    }
}
