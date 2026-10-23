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
            _ = client.GetBiosInformation(); _ = await new WindowsDomainClient().GetMembershipAsync(token);
        }, token);
        if (options.Includes("processes")) await report.Check("processes.snapshot", () =>
        {
            var client = new WindowsProcessClient();
            Require(client.Enumerate().Any(p => p.ProcessId == Environment.ProcessId), "Current process absent.");
            Require(client.Find(int.MaxValue) is null, "Nonexistent process returned.");
            return Task.CompletedTask;
        }, token);
        if (options.Includes("networks")) await report.Check("networks.enumerate", async () => { _ = await new WindowsNetworkClient().EnumerateAsync(token); }, token);
        if (options.Includes("shortcuts")) await report.Check("shortcuts.roundtrip", Shortcuts, token);
        if (options.Includes("services"))
        {
            await report.Check("services.inspect", async () =>
            {
                var client = new WindowsServiceClient();
                var rows = await client.EnumerateAsync(token);
                Require(rows.Length > 0, "Service enumeration unexpectedly empty.");
                Require(await client.FindAsync(report.Prefix, token) is null, "Unique missing service was found.");
                var service = await client.FindAsync("EventLog", token);
                Require(service is not null && service.BinaryCommandLine.Length > 0, "EventLog configuration missing.");
            }, token);
            if (options.Changes) await report.Check("services.lifecycle", Services, token);
            else if (options.ExpectDenied) await report.Check("services.denied", ServicesDenied, token);
            else report.Skip("services.lifecycle", "Requires --allow-changes on a disposable VM.");
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
            if (options.Changes) await report.Check("tasks.roundtrip-and-execution", Tasks, token);
            else if (options.ExpectDenied) await report.Check("tasks.denied", TasksDenied, token);
            else report.Skip("tasks.roundtrip-and-execution", "Requires --allow-changes.");
        }
        if (options.Includes("firewall"))
        {
            await report.Check("firewall.inspect", async () =>
            {
                var client = new WindowsFirewallClient();
                Require((await client.GetProfilesAsync(token)).Length == 3, "Expected three firewall profiles.");
                _ = await client.EnumerateAsync(token);
            }, token);
            if (options.Changes) await report.Check("firewall.roundtrip", Firewall, token);
            else if (options.ExpectDenied) await report.Check("firewall.denied", FirewallDenied, token);
            else report.Skip("firewall.roundtrip", "Requires --allow-changes.");
        }
        if (options.Includes("shares"))
        {
            await report.Check("shares.inspect", async () => { _ = await new WindowsShareClient().EnumerateAsync(token); }, token);
            if (options.Changes) await report.Check("shares.explicit-security", Shares, token);
            else if (options.ExpectDenied) await report.Check("shares.denied", SharesDenied, token);
            else report.Skip("shares.explicit-security", "Requires --allow-changes.");
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
            await client.CreateAsync(new(name, command, "LocalSystem") { Description = "Runic disposable verification service" }, cancellationToken: token);
            created = true;
            var service = await client.FindAsync(name, token) ?? throw new InvalidOperationException("Created service missing.");
            Require(service.Status.State == ServiceState.Stopped && service.BinaryCommandLine == command, "New service state/configuration mismatch.");
            await Conflict(() => client.CreateAsync(new(name, command, "LocalSystem"), cancellationToken: token));
            await client.UpdateAsync(name, new() { Description = "updated", FailurePolicy = new(TimeSpan.FromDays(1), [new(ServiceFailureActionKind.None, TimeSpan.Zero)]) }, cancellationToken: token);
            Require((await client.FindAsync(name, token))?.Description == "updated", "Service description update missing.");
            await client.StartAsync(name, token);
            var running = await client.WaitForStateAsync(name, ServiceState.Running, TimeSpan.FromSeconds(30), token);
            Require(running.ProcessId != 0 && new WindowsProcessClient().Find(running.ProcessId) is not null, "Service process ID missing.");
            await client.PauseAsync(name, token); await client.WaitForStateAsync(name, ServiceState.Paused, TimeSpan.FromSeconds(10), token);
            await client.ContinueAsync(name, token); await client.WaitForStateAsync(name, ServiceState.Running, TimeSpan.FromSeconds(10), token);
            await client.StopAsync(name, token); await client.WaitForStateAsync(name, ServiceState.Stopped, TimeSpan.FromSeconds(30), token);
        }
        finally
        {
            if (created) await report.Check("cleanup.service", async () =>
            {
                Exception? stopFailure = null;
                try
                {
                    var state = await client.FindStatusAsync(name);
                    if (state is not null && state.State != ServiceState.Stopped)
                    {
                        if (state.State != ServiceState.StopPending) await client.StopAsync(name);
                        await client.WaitForStateAsync(name, ServiceState.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
                catch (Exception error) when (error is WindowsAdministrationException or TimeoutException) { stopFailure = error; }
                // DeleteService also marks a running service; the SCM removes it once its process ends.
                Require(await client.DeleteAsync(name), "Created service unexpectedly absent before cleanup.");
                if (stopFailure is not null)
                {
                    report.Resource("  service stop failed during cleanup: " + stopFailure.Message);
                    if (await client.FindStatusAsync(name) is { ProcessId: not 0 } running)
                    {
                        // Only this verifier's own executable is ended; FixtureProcess checks the image path.
                        FixtureProcess.Terminate(running.ProcessId, Executable);
                        report.Resource("  ended the fixture service process " + running.ProcessId);
                    }
                }
                await Until(async () => await client.FindStatusAsync(name) is null, TimeSpan.FromSeconds(30), default);
                Require(!await client.DeleteAsync(name), "Second service deletion did not report absence.");
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
            Enabled = false, ApplicationPath = Executable, Protocol = FirewallProtocol.Tcp, LocalPorts = "49199", RemoteAddresses = "127.0.0.1",
            Grouping = report.Prefix, Description = "fixture"
        };
        var identity = new FirewallRuleIdentity(spec.Name, spec.Grouping, spec.ApplicationPath, spec.ServiceName, spec.Direction);
        Require(await client.FindAsync(spec.Name, token) is null, "Fixture firewall name already exists; refusing to touch it.");
        var created = false;
        report.Resource("Disabled firewall rule: " + report.Prefix);
        try
        {
            await client.CreateAsync(spec, token);
            created = true;
            var before = await client.FindAsync(spec.Name, token) ?? throw new InvalidOperationException("New firewall rule missing.");
            await Conflict(() => client.CreateAsync(spec, token));
            await client.UpdateAsync(identity, new() { Description = "updated", LocalPorts = "49200" }, token);
            var after = await client.FindAsync(spec.Name, token) ?? throw new InvalidOperationException("Updated rule missing.");
            Require(after.Configuration.Description == "updated" && after.Configuration.LocalPorts == "49200" &&
                after.Configuration.ApplicationPath == before.Configuration.ApplicationPath && !after.Configuration.Enabled, "Firewall update lost settings.");
            // An update must not turn the rule into an inbound allow rule without a program, service or local port.
            await report.Check("firewall.update-scope", async () =>
            {
                try
                {
                    await client.UpdateAsync(identity, new() { Action = FirewallAction.Allow, ApplicationPath = "", LocalPorts = "" }, token);
                    throw new InvalidOperationException("An update to an unscoped inbound allow rule was accepted.");
                }
                catch (ArgumentException) { }
                var unchanged = await client.FindAsync(spec.Name, token) ?? throw new InvalidOperationException("Rule missing after the refused update.");
                Require(unchanged.Configuration.Action == FirewallAction.Block && unchanged.Configuration.ApplicationPath == before.Configuration.ApplicationPath &&
                    unchanged.Configuration.LocalPorts == "49200", "The refused update changed the rule.");
            }, token);
        }
        finally
        {
            await report.Check("cleanup.firewall", async () =>
            {
                // Check even after Add throws, but delete only our exact identity.
                var observed = await client.FindAsync(spec.Name);
                if (observed is not null)
                {
                    Require(observed.Identity == identity, "Firewall identity changed; refusing cleanup.");
                    Require(await client.DeleteAsync(identity), "Created rule unexpectedly absent before cleanup.");
                }
                else Require(!created, "Created rule unexpectedly absent before cleanup.");
                Require(!await client.DeleteAsync(identity), "Second rule deletion did not report absence.");
                // Delete renames the checked rule before removing it; nothing of the fixture may remain under any name.
                Require(!(await client.EnumerateAsync()).Any(rule => rule.Identity.Grouping == report.Prefix), "A fixture rule remained after deletion.");
            });
        }
    }
    private async Task Shares()
    {
        var client = new WindowsShareClient();
        const string suffix = "acl";
        var name = report.Prefix + "-" + suffix;
        var path = Path.Combine(report.Folder, "share-" + suffix);
        var created = false;
        Directory.CreateDirectory(path);
        report.Resource("SMB share: " + name + "; directory: " + path);
        var initialSddl = "O:BAG:BAD:(A;;FA;;;BA)(A;;FR;;;BU)";
        var spec = new ShareSpecification(name, path, Descriptor(initialSddl));
        try
        {
            await client.CreateAsync(spec, token); created = true;
            var before = await client.FindAsync(name, token) ?? throw new InvalidOperationException("Created share missing.");
            CheckDescriptor(before, initialSddl);
            await Conflict(() => client.CreateAsync(spec, token));
            // A metadata-only update must preserve the stored descriptor.
            await client.UpdateAsync(name, new() { Description = "updated" }, token);
            var after = await client.FindAsync(name, token) ?? throw new InvalidOperationException("Updated share missing.");
            Require(after.Description == "updated" && SameDescriptor(before.SecurityDescriptor, after.SecurityDescriptor),
                "Metadata update changed stored security.");
            var changedSddl = "O:BAG:BAD:(A;;FA;;;BA)(A;;FW;;;BU)";
            await client.UpdateAsync(name, new() { SecurityDescriptor = Descriptor(changedSddl) }, token);
            after = await client.FindAsync(name, token) ?? throw new InvalidOperationException("Share disappeared after security update.");
            CheckDescriptor(after, changedSddl);
            await client.UpdateAsync(name, new() { SecurityDescriptor = after.SecurityDescriptor }, token);
            var repeated = await client.FindAsync(name, token) ?? throw new InvalidOperationException("Share disappeared after repeated update.");
            Require(SameDescriptor(after.SecurityDescriptor, repeated.SecurityDescriptor), "Repeated security update changed SID/ACE representation.");
        }
        finally
        {
            if (created) await report.Check("cleanup.share-" + suffix, async () =>
            {
                Require(await client.DeleteAsync(name), "Created share unexpectedly absent before cleanup.");
                Require(!await client.DeleteAsync(name), "Second share deletion did not report absence.");
                Directory.Delete(path);
            });
            else if (Directory.Exists(path)) Directory.Delete(path);
        }
    }
    // --expect-denied: a non-elevated caller attempts each owned write. Windows must refuse it
    // with AccessDenied, and an independent lookup must find no resource afterwards.
    private async Task ServicesDenied()
    {
        var client = new WindowsServiceClient();
        var name = report.Prefix;
        report.Resource("Denied service attempt: " + name);
        await Denied(() => client.CreateAsync(new(name, "\"" + Executable + "\" --service " + name, "LocalSystem"), cancellationToken: token),
            async () => Require(await client.DeleteAsync(name), "Unexpectedly created service was already absent."));
        Require(await client.FindStatusAsync(name, token) is null, "Denied service creation left a service behind.");
    }
    private async Task TasksDenied()
    {
        var client = new WindowsTaskSchedulerClient();
        var path = "\\" + report.Prefix;
        report.Resource("Denied task attempt: " + path);
        var spec = new ScheduledTaskSpecification(new("SYSTEM", TaskLogonType.ServiceAccount),
            [new(Executable, "--help")], []) { Description = "fixture", ExecutionTimeLimit = TimeSpan.FromMinutes(1) };
        await Denied(() => client.CreateAsync(path, spec, cancellationToken: token),
            async () => Require(await client.DeleteAsync(path), "Unexpectedly registered task was already absent."));
        Require(await client.FindAsync(path, token) is null, "Denied task registration left a task behind.");
    }
    private async Task FirewallDenied()
    {
        var client = new WindowsFirewallClient();
        var spec = new FirewallRuleSpecification(report.Prefix, FirewallDirection.Inbound, FirewallAction.Block)
        {
            Enabled = false, ApplicationPath = Executable, Protocol = FirewallProtocol.Tcp, LocalPorts = "49199", RemoteAddresses = "127.0.0.1",
            Grouping = report.Prefix, Description = "fixture"
        };
        Require(await client.FindAsync(spec.Name, token) is null, "Fixture firewall name already exists; refusing to touch it.");
        report.Resource("Denied firewall rule attempt: " + spec.Name);
        var identity = new FirewallRuleIdentity(spec.Name, spec.Grouping, spec.ApplicationPath, spec.ServiceName, spec.Direction);
        await Denied(() => client.CreateAsync(spec, token), async () =>
        {
            // Delete only the exact rule this run attempted.
            var observed = await client.FindAsync(spec.Name) ?? throw new InvalidOperationException("Unexpectedly created rule was already absent.");
            Require(observed.Identity == identity, "Firewall identity changed; refusing rollback.");
            Require(await client.DeleteAsync(identity), "Unexpectedly created rule was already absent.");
        });
        Require(await client.FindAsync(spec.Name, token) is null, "Denied firewall creation left a rule behind.");
    }
    private async Task SharesDenied()
    {
        var client = new WindowsShareClient();
        var name = report.Prefix + "-denied";
        var path = Path.Combine(report.Folder, "share-denied");
        Directory.CreateDirectory(path);
        report.Resource("Denied SMB share attempt: " + name + "; directory: " + path);
        try
        {
            await Denied(() => client.CreateAsync(new ShareSpecification(name, path, Descriptor("O:BAG:BAD:(A;;FA;;;BA)(A;;FR;;;BU)")), token),
                async () => Require(await client.DeleteAsync(name), "Unexpectedly created share was already absent."));
            // Enumeration (level 1) needs no administrative rights, unlike reading a share's security.
            Require(!(await client.EnumerateAsync(token)).Any(share => string.Equals(share.Name, name, StringComparison.OrdinalIgnoreCase)), "Denied share creation left a share behind.");
        }
        finally { Directory.Delete(path); }
    }
    // When the write unexpectedly succeeds (for example with delegated rights), roll back the
    // uniquely named resource immediately and still fail the check.
    private async Task Denied(Func<Task> action, Func<Task> rollback)
    {
        try { await action(); }
        catch (WindowsAdministrationException error) when (error.Category == AdministrationErrorCategory.AccessDenied)
        {
            report.Resource($"  denied as expected: {error.Operation}; {error.NativeErrorDomain} 0x{error.NativeErrorCode:X8}");
            return;
        }
        catch (WindowsAdministrationException error)
        {
            throw new WindowsAdministrationException(error.Operation, error.Category, error.NativeErrorDomain,
                error.NativeErrorCode, $"Expected AccessDenied, received {error.Category}. {error.Message}", error);
        }
        string outcome;
        try { await rollback(); outcome = "the created resource was deleted"; }
        catch (Exception error) when (error is WindowsAdministrationException or InvalidOperationException) { outcome = "rollback failed (" + error.Message + "); inspect the resource journal and revert the fixture"; }
        report.Resource("  write unexpectedly succeeded; " + outcome);
        throw new InvalidOperationException("The non-elevated write unexpectedly succeeded; " + outcome + ".");
    }
    private static WindowsSecurityDescriptor Descriptor(string sddl) => WindowsSecurityDescriptor.FromSddl(sddl);
    private static void CheckDescriptor(ShareSnapshot share, string expected)
    {
        var actual = share.SecurityDescriptor ?? throw new InvalidOperationException("Explicit share security descriptor missing.");
        Require(actual.Sddl == WindowsSecurityDescriptor.FromSddl(expected).Sddl, "Explicit share ACL did not roundtrip.");
    }
    // WindowsSecurityDescriptor compares the exact binary form: SIDs, ACE order and flags.
    private static bool SameDescriptor(WindowsSecurityDescriptor? left, WindowsSecurityDescriptor? right) => left == right;
    internal static async Task Conflict(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Duplicate creation unexpectedly succeeded."); }
        catch (WindowsAdministrationException error)
        {
            if (error.Category != AdministrationErrorCategory.Conflict)
                throw new WindowsAdministrationException(error.Operation, error.Category, error.NativeErrorDomain,
                    error.NativeErrorCode, $"Duplicate creation expected Conflict, received {error.Category}. {error.Message}", error);
        }
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
