using Runic.Platform.Administration.Windows;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Networks;
using Runic.Platform.Administration.Windows.Processes;
using Runic.Platform.Administration.Windows.Services;
using Runic.Platform.Administration.Windows.Shares;
using Runic.Platform.Administration.Windows.Shortcuts;
using Runic.Platform.Administration.Windows.SystemInformation;
using Runic.Platform.Administration.Windows.Tasks;
using static RunReport;

internal sealed class LocalChecks(RunReport report, Options options, CancellationToken token)
{
    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
    internal async Task RunAsync()
    {
        if (options.Includes("system")) await report.Check("system.inventory", async () =>
        {
            var client = new WindowsSystemInformationClient();
            var os = client.GetOperatingSystem();
            Require(os.Version.Major == Environment.OSVersion.Version.Major && os.Version.Build == Environment.OSVersion.Version.Build, "Native OS version mismatch.");
            var name = await client.GetOperatingSystemNameAsync(token);
            Require(name.Length != 0, "Empty OS name.");
            report.Resource("Observed OS: " + name);
            _ = client.GetBiosInformation(); _ = new WindowsDomainClient().GetMembership();
        }, token);
        if (options.Includes("processes")) await report.Check("processes.snapshot", () =>
        {
            var client = new WindowsProcessClient();
            Require(client.Enumerate().Any(p => p.ProcessId == Environment.ProcessId), "Current process absent.");
            Require(client.Find(uint.MaxValue) is null, "Nonexistent process returned.");
            return Task.CompletedTask;
        }, token);
        if (options.Includes("networks")) await report.Check("networks.enumerate", async () => { _ = await new WindowsNetworkClient().EnumerateAsync(token); }, token);
        if (options.Includes("shortcuts")) await report.Check("shortcuts.roundtrip", Shortcuts, token);
        if (options.Includes("services"))
        {
            await report.Check("services.inspect", () =>
            {
                var client = new WindowsServiceClient();
                var rows = client.Enumerate();
                Require(rows.Length > 0, "Service enumeration unexpectedly empty.");
                Require(client.Find(report.Prefix) is null, "Unique missing service was found.");
                var service = client.Find("EventLog");
                Require(service is not null && service.BinaryCommandLine.Length > 0, "EventLog configuration missing.");
                return Task.CompletedTask;
            }, token);
            if (options.Changes) await report.Check("services.lifecycle", Services, token); else report.Skip("services.lifecycle", "Requires --allow-changes on a disposable VM.");
        }
        if (options.Includes("tasks"))
        {
            await report.Check("tasks.inspect", async () =>
            {
                var client = new WindowsTaskSchedulerClient();
                _ = await client.EnumerateFoldersAsync(cancellationToken: token); _ = await client.EnumerateAsync(cancellationToken: token);
                Require(await client.FindAsync("\\" + report.Prefix, token) is null, "Unique missing task was found.");
                await client.ValidateAsync(new(new("SYSTEM", TaskLogonType.ServiceAccount), [new(Executable, "--help")], []), token);
            }, token);
            if (options.Changes) await report.Check("tasks.roundtrip-and-execution", Tasks, token); else report.Skip("tasks.roundtrip-and-execution", "Requires --allow-changes.");
        }
        if (options.Includes("firewall"))
        {
            await report.Check("firewall.inspect", async () =>
            {
                var client = new WindowsFirewallClient();
                Require((await client.GetProfilesAsync(token)).Length == 3, "Expected three firewall profiles.");
                _ = await client.EnumerateAsync(token);
            }, token);
            if (options.Changes) await report.Check("firewall.roundtrip", Firewall, token); else report.Skip("firewall.roundtrip", "Requires --allow-changes.");
        }
        if (options.Includes("shares"))
        {
            await report.Check("shares.inspect", () => { _ = new WindowsShareClient().Enumerate(); return Task.CompletedTask; }, token);
            if (options.Changes) await report.Check("shares.roundtrip-security", Shares, token); else report.Skip("shares.roundtrip-security", "Requires --allow-changes.");
        }
    }
    private async Task Shortcuts()
    {
        var path = Path.Combine(report.Folder, "Überprüfung.lnk");
        var target = Path.Combine(report.Folder, "Grüße.txt");
        var client = new WindowsShellLinkClient();
        try
        {
            await File.WriteAllTextAsync(target, "fixture", token);
            var spec = new ShellLinkSpecification(target) { Arguments = "--name \"Schüler\"", WorkingDirectory = report.Folder, Description = "Überprüfung", Icon = new(target, -3), Hotkey = 0x0241 };
            Require(await client.FindAsync(path, token) is null, "New link already exists.");
            await client.CreateAsync(path, spec, cancellationToken: token);
            var before = await client.FindAsync(path, token) ?? throw new InvalidOperationException("Link missing after creation.");
            Require(before.Arguments == spec.Arguments && before.TargetPath == target && before.Hotkey == spec.Hotkey && before.Icon == spec.Icon, "Link metadata mismatch.");
            await Conflict(() => client.CreateAsync(path, spec, cancellationToken: token));
            await client.UpdateAsync(path, new() { Description = "updated" }, token);
            Require(await client.FindAsync(path, token) == before with { Description = "updated" }, "Selected update lost link metadata.");
            await client.CreateAsync(path, spec with { TargetPath = @"\\runic-invalid-host\share\target.txt" }, true, token);
            Require((await client.FindAsync(path, token))?.TargetPath == @"\\runic-invalid-host\share\target.txt", "UNC target mismatch.");
            await File.WriteAllTextAsync(path, "invalid", token);
            try { _ = await client.FindAsync(path, token); throw new InvalidOperationException("Malformed link accepted."); }
            catch (WindowsAdministrationException) { }
        }
        finally { await report.Check("cleanup.shortcuts", () => { File.Delete(path); File.Delete(target); return Task.CompletedTask; }); }
    }
    private async Task Services()
    {
        var client = new WindowsServiceClient();
        var name = report.Prefix;
        var created = false;
        report.Resource("Service: " + name);
        try
        {
            var command = "\"" + Executable + "\" --service " + name;
            client.Create(new(name, command) { Description = "Runic disposable verification service" });
            created = true;
            var service = client.Find(name) ?? throw new InvalidOperationException("Created service missing.");
            Require(service.Status.State == ServiceState.Stopped && service.BinaryCommandLine == command, "New service state/configuration mismatch.");
            await Conflict(() => { client.Create(new(name, command)); return Task.CompletedTask; });
            client.Update(name, new() { Description = "updated", FailurePolicy = new(TimeSpan.FromDays(1), [new(ServiceFailureActionKind.None, TimeSpan.Zero)]) });
            Require(client.Find(name)?.Description == "updated", "Service description update missing.");
            client.Start(name);
            var running = await client.WaitForStateAsync(name, ServiceState.Running, TimeSpan.FromSeconds(30), token);
            Require(running.ProcessId != 0 && new WindowsProcessClient().Find(running.ProcessId) is not null, "Service process ID missing.");
            client.Pause(name); await client.WaitForStateAsync(name, ServiceState.Paused, TimeSpan.FromSeconds(10), token);
            client.Continue(name); await client.WaitForStateAsync(name, ServiceState.Running, TimeSpan.FromSeconds(10), token);
            client.Stop(name); await client.WaitForStateAsync(name, ServiceState.Stopped, TimeSpan.FromSeconds(30), token);
        }
        finally
        {
            if (created) await report.Check("cleanup.service", async () =>
            {
                var state = client.FindStatus(name);
                if (state is not null && state.State != ServiceState.Stopped)
                {
                    if (state.State != ServiceState.StopPending) client.Stop(name);
                    await client.WaitForStateAsync(name, ServiceState.Stopped, TimeSpan.FromSeconds(30));
                }
                Require(client.Delete(name), "Created service unexpectedly absent before cleanup.");
                await Until(() => Task.FromResult(client.FindStatus(name) is null), TimeSpan.FromSeconds(20), default);
                Require(!client.Delete(name), "Second service deletion did not report absence.");
            });
        }
    }
    private async Task Tasks()
    {
        var client = new WindowsTaskSchedulerClient();
        var folder = "\\" + report.Prefix; var path = folder + "\\Task";
        var marker = Path.Combine(report.Folder, "task-marker.txt");
        bool folderCreated = false, taskCreated = false;
        report.Resource("Task folder: " + folder);
        try
        {
            await client.CreateFolderAsync(folder, token); folderCreated = true;
            var spec = new ScheduledTaskSpecification(new("SYSTEM", TaskLogonType.ServiceAccount),
                [new(Executable, "--task-marker \"" + marker + "\"")], []) { Description = "fixture", ExecutionTimeLimit = TimeSpan.FromMinutes(1) };
            var original = await client.CreateAsync(path, spec, cancellationToken: token); taskCreated = true;
            await Conflict(() => client.CreateAsync(path, spec, cancellationToken: token));
            var updated = await client.UpdateAsync(path, new() { Description = "updated" }, cancellationToken: token);
            var before = System.Xml.Linq.XDocument.Parse(original.Xml); var after = System.Xml.Linq.XDocument.Parse(updated.Xml);
            var ns = before.Root!.Name.Namespace;
            Require(System.Xml.Linq.XNode.DeepEquals(before.Root.Element(ns + "Actions"), after.Root!.Element(ns + "Actions")), "Task update changed actions.");
            await client.SetEnabledAsync(path, false, token); Require((await client.FindAsync(path, token))?.Enabled is false, "Task was not disabled.");
            await client.SetEnabledAsync(path, true, token);
            _ = await client.RunAsync(path, token);
            await Until(async () =>
            {
                var observed = await client.FindAsync(path, token);
                return File.Exists(marker) && observed is { State: ScheduledTaskState.Ready, LastTaskResult: 0, LastRunTime: not null };
            }, TimeSpan.FromSeconds(30), token);
        }
        finally
        {
            if (taskCreated) await report.Check("cleanup.task", async () =>
            {
                var current = await client.FindAsync(path);
                if (current?.State == ScheduledTaskState.Running) await client.StopAsync(path);
                Require(await client.DeleteAsync(path), "Created task unexpectedly absent before cleanup.");
                Require(!await client.DeleteAsync(path), "Second task deletion did not report absence.");
            });
            if (folderCreated) await report.Check("cleanup.task-folder", async () => { Require(await client.DeleteFolderAsync(folder), "Task folder absent before cleanup."); });
            await report.Check("cleanup.task-marker", () => { File.Delete(marker); return Task.CompletedTask; });
        }
    }
    private async Task Firewall()
    {
        var client = new WindowsFirewallClient();
        var spec = new FirewallRuleSpecification(report.Prefix, FirewallDirection.Inbound, FirewallAction.Block)
        {
            Enabled = false, ApplicationPath = Executable, Protocol = 6, LocalPorts = "49199", RemoteAddresses = "127.0.0.1",
            Grouping = report.Prefix, Description = "fixture"
        };
        FirewallRuleIdentity? identity = null;
        report.Resource("Disabled firewall rule: " + report.Prefix);
        try
        {
            await client.CreateAsync(spec, token);
            identity = new(spec.Name, spec.Grouping, spec.ApplicationPath, spec.ServiceName, spec.Direction);
            var before = await client.FindAsync(spec.Name, token) ?? throw new InvalidOperationException("New firewall rule missing.");
            await Conflict(() => client.CreateAsync(spec, token));
            await client.UpdateAsync(identity, new() { Description = "updated", LocalPorts = "49200" }, token);
            var after = await client.FindAsync(spec.Name, token) ?? throw new InvalidOperationException("Updated rule missing.");
            Require(after.Configuration.Description == "updated" && after.Configuration.LocalPorts == "49200" &&
                after.Configuration.ApplicationPath == before.Configuration.ApplicationPath && !after.Configuration.Enabled, "Firewall update lost settings.");
        }
        finally
        {
            if (identity is not null) await report.Check("cleanup.firewall", async () =>
            {
                Require(await client.DeleteAsync(identity), "Created rule unexpectedly absent before cleanup.");
                Require(!await client.DeleteAsync(identity), "Second rule deletion did not report absence.");
            });
        }
    }
    private async Task Shares()
    {
        var client = new WindowsShareClient();
        var path = Path.Combine(report.Folder, "share");
        var created = false;
        Directory.CreateDirectory(path);
        report.Resource("SMB share: " + report.Prefix + "; directory: " + path);
        try
        {
            client.Create(new(report.Prefix, path)); created = true;
            var before = client.Find(report.Prefix) ?? throw new InvalidOperationException("Created share missing.");
            Require(before.SecurityDescriptor.Length != 0, "Share security descriptor missing.");
            await Conflict(() => { client.Create(new(report.Prefix, path)); return Task.CompletedTask; });
            client.Update(report.Prefix, new() { Description = "updated", SecurityDescriptor = before.SecurityDescriptor });
            var after = client.Find(report.Prefix) ?? throw new InvalidOperationException("Updated share missing.");
            Require(after.Description == "updated" && after.SecurityDescriptor.AsSpan().SequenceEqual(before.SecurityDescriptor.AsSpan()), "Share update changed SID/ACE representation.");
        }
        finally
        {
            if (created) await report.Check("cleanup.share", () =>
            {
                Require(client.Delete(report.Prefix), "Created share unexpectedly absent before cleanup.");
                Require(!client.Delete(report.Prefix), "Second share deletion did not report absence.");
                Directory.Delete(path); return Task.CompletedTask;
            });
            else if (Directory.Exists(path)) Directory.Delete(path);
        }
    }
    internal static async Task Conflict(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Duplicate creation unexpectedly succeeded."); }
        catch (WindowsAdministrationException error) { Require(error.Category == AdministrationErrorCategory.Conflict, "Duplicate creation failed with " + error.Category + " instead of Conflict."); }
    }
    internal static async Task Until(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await predicate())
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("Expected native state was not observed within " + timeout);
            await Task.Delay(200, cancellationToken);
        }
    }
}
