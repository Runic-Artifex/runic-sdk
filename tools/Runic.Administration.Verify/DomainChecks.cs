using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Dns;
using Runic.Platform.Administration.Windows.GroupPolicy;
using static RunReport;

internal sealed class DomainChecks(RunReport report, Options options, CancellationToken token)
{
    internal async Task RunAsync()
    {
        var directory = new WindowsDirectoryClient(new(options.Server!));
        var ad = new ActiveDirectoryClient(directory);
        var fixtureDn = "OU=" + report.Prefix + "," + options.BaseDn;
        var fixtureCreated = false;
        if (options.Includes("ldap")) await report.Check("ldap.inspect", async () =>
        {
            var root = await directory.ReadRootDseAsync(["defaultNamingContext", "dnsHostName"], token);
            Require(root is not null, "RootDSE absent.");
            if (options.BaseDn is not null)
                Require(await directory.FindAsync(options.BaseDn, ["distinguishedName"], token) is not null, "Explicit base DN absent.");
        }, token);
        if (options.Includes("gpo")) await report.Check("gpo.inspect", async () =>
        {
            var client = new WindowsGroupPolicyClient(options.Domain!, options.Server!);
            _ = await client.EnumerateAsync(token);
            Require(await client.FindAsync(Guid.NewGuid(), token) is null, "Random GPO ID unexpectedly found.");
        }, token);
        if (options.Includes("dns")) await report.Check("dns.inspect", async () =>
        {
            var client = new WindowsDnsClient(options.DnsServer ?? options.Server!);
            _ = await client.EnumerateZonesAsync(token);
            if (options.DnsZone is not null)
            {
                Require(await client.FindZoneAsync(options.DnsZone, token) is not null, "Explicit DNS zone absent.");
                _ = await client.EnumerateRecordsAsync(options.DnsZone, cancellationToken: token);
            }
        }, token);

        if (!options.Changes)
        {
            report.Skip("domain.mutations", "Requires --allow-changes and an existing --base-dn on a disposable domain fixture.");
            return;
        }
        try
        {
            if (options.Includes("ldap") || options.Includes("gpo"))
                await report.Check("domain.create-fixture-ou", async () =>
                {
                    Require(await directory.FindAsync(options.BaseDn!, ["distinguishedName"], token) is not null, "Base DN absent.");
                    report.Resource("Fixture OU: " + fixtureDn);
                    await ad.CreateOrganizationalUnitAsync(options.BaseDn!, report.Prefix, token);
                    fixtureCreated = true;
                }, token);
            if (options.Includes("ldap"))
            {
                if (fixtureCreated) await report.Check("ldap.objects-membership-password", () => Ldap(directory, ad, fixtureDn), token);
                else report.Skip("ldap.objects-membership-password", "Fixture OU creation failed or canceled.");
            }
            if (options.Includes("gpo"))
            {
                if (fixtureCreated) await report.Check("gpo.backup-import-restore-links", () => Gpo(fixtureDn), token);
                else report.Skip("gpo.backup-import-restore-links", "Fixture OU creation failed or canceled.");
            }
            if (options.Includes("dns"))
            {
                if (options.DnsZone is null) report.Skip("dns.record-roundtrips", "Supply an existing disposable --dns-zone.");
                else await Dns();
            }
        }
        finally
        {
            if (fixtureCreated) await report.Check("cleanup.fixture-ou", async () =>
            {
                Require(await directory.DeleteAsync(fixtureDn), "Fixture OU unexpectedly absent before cleanup.");
            });
        }
    }
    private async Task Ldap(WindowsDirectoryClient directory, ActiveDirectoryClient ad, string parent)
    {
        var owned = new List<string>();
        var shortId = report.Id[..12];
        var userDn = "CN=User," + parent; var groupDn = "CN=Group," + parent;
        try
        {
            await ad.CreateUserAsync(parent, "User", "rvu" + shortId, cancellationToken: token); owned.Add(userDn);
            await ad.CreateGroupAsync(parent, "Group", "rvg" + shortId, ActiveDirectoryGroupScope.Global, true, cancellationToken: token); owned.Add(groupDn);
            var computerDn = "CN=Computer," + parent;
            await ad.CreateComputerAsync(parent, "Computer", "rvc" + shortId + "$", token); owned.Add(computerDn);
            foreach (var dn in owned) report.Resource("AD object: " + dn);
            var rows = await directory.SearchAsync(new(parent, "(objectClass=*)", DirectorySearchScope.OneLevel, ["distinguishedName", "objectClass"]) { PageSize = 1 }, token);
            Require(rows.Length == 3, "Paging did not return all three fixture objects.");
            await ad.AddMemberAsync(groupDn, userDn, token);
            Require((await ad.GetMembersAsync(groupDn, token)).Any(value => Text(value).Equals(userDn, StringComparison.OrdinalIgnoreCase)), "Group membership missing.");
            await ad.RemoveMemberAsync(groupDn, userDn, token);
            Require((await ad.GetMembersAsync(groupDn, token)).Length == 0, "Group member was not removed.");
            var spn = "runic-verify/" + report.Id + "." + options.Domain;
            await ad.AddServicePrincipalNamesAsync(computerDn, [spn], token);
            Require((await ad.GetServicePrincipalNamesAsync(computerDn, token)).Any(value => Text(value) == spn), "SPN missing.");
            await ad.RemoveServicePrincipalNamesAsync(computerDn, [spn], token);
            await directory.ModifyAsync(userDn, [new("description", DirectoryModificationKind.Replace, [new DirectoryValue.Text("Grüße")])], token);
            var renamed = "CN=Renamed," + parent;
            await directory.MoveAsync(userDn, parent, "CN=Renamed", token);
            owned[0] = renamed; report.Resource("Renamed AD user: " + renamed);
            Require(await directory.FindAsync(userDn, ["distinguishedName"], token) is null, "Old DN still exists after rename.");
            var snapshot = await directory.FindAsync(renamed, ["description", "objectSid"], token);
            Require(snapshot is not null && snapshot.Attributes["objectSid"][0] is DirectoryValue.Binary, "SID was not returned as binary.");
            // Generated only in memory; never included in reports or command-line arguments.
            var password = "Rv!a9-" + Guid.NewGuid().ToString("N");
            await ad.ResetPasswordAsync(renamed, password, token);
            await ad.SetAccountEnabledAsync(renamed, true, token);
            await ad.SetAccountEnabledAsync(renamed, false, token);
            await report.Check("ldap.change-password-policy", () => ad.ChangePasswordAsync(renamed, password, "Rv!b8-" + Guid.NewGuid().ToString("N"), token), token);
        }
        finally
        {
            foreach (var dn in owned.AsEnumerable().Reverse())
                await report.Check("cleanup.ad-object " + dn, async () => { Require(await directory.DeleteAsync(dn), "Created AD object unexpectedly absent."); });
        }
    }
    private async Task Gpo(string targetDn)
    {
        var client = new WindowsGroupPolicyClient(options.Domain!, options.Server!);
        var owned = new List<Guid>();
        var linked = new List<Guid>();
        var backupDirectory = Path.Combine(report.Folder, "gpo-backup");
        Directory.CreateDirectory(backupDirectory);
        try
        {
            var first = await client.CreateAsync(report.Prefix + "-source", token); owned.Add(first.Id); report.Resource("GPO: " + first.Id);
            var second = await client.CreateAsync(report.Prefix + "-destination", token); owned.Add(second.Id); report.Resource("GPO: " + second.Id);
            await client.SetEnabledAsync(first.Id, false, false, token);
            await client.SetEnabledAsync(second.Id, false, false, token);
            var backup = await client.BackupAsync(first.Id, backupDirectory, "Runic disposable fixture", token);
            report.Resource("GPO backup: " + backup.Value.BackupId + " at " + backupDirectory);
            var anotherBackup = await client.BackupAsync(first.Id, backupDirectory, "Second backup", token);
            var backups = await WindowsGroupPolicyClient.EnumerateBackupsAsync(backupDirectory, token);
            Require(backups.Length == 2, "Backup enumeration must include older backups, not only the latest.");
            foreach (var expected in new[] { backup.Value, anotherBackup.Value })
            {
                var actual = backups.Single(value => value.BackupId == expected.BackupId);
                Require(actual.GroupPolicyId == expected.GroupPolicyId && actual.DisplayName == expected.DisplayName
                    && actual.Comment == expected.Comment && actual.Timestamp == expected.Timestamp,
                    "Backup enumeration lost metadata.");
            }
            var imported = await client.ImportAsync(second.Id, backupDirectory, backup.Value.BackupId, cancellationToken: token);
            Require(imported.Value.Id == second.Id, "Import changed destination identity.");
            var restored = await client.RestoreAsync(backupDirectory, backup.Value.BackupId, token);
            Require(restored.Value.Id == first.Id, "Restore changed original identity.");
            var xml = await client.GenerateReportAsync(first.Id, GroupPolicyReportFormat.Xml, token);
            Require(System.Xml.Linq.XDocument.Parse(xml.Value).Root is not null, "GPMC returned no XML report.");
            _ = await client.GetPermissionsAsync(first.Id, token);
            await client.SetWmiFilterAsync(first.Id, null, token);
            foreach (var id in owned) { await client.CreateLinkAsync(id, targetDn, cancellationToken: token); linked.Add(id); }
            await client.SetLinkOrderAsync(targetDn, [second.Id, first.Id], token);
            Require((await client.GetLinksAsync(targetDn, token)).Select(link => link.GroupPolicyId).SequenceEqual([second.Id, first.Id]), "Link precedence mismatch.");
            await client.UpdateLinkAsync(first.Id, targetDn, enabled: false, enforced: true, cancellationToken: token);
            var link = (await client.GetLinksAsync(targetDn, token)).Single(link => link.GroupPolicyId == first.Id);
            Require(!link.Enabled && link.Enforced, "Link flags mismatch.");
            await client.SetInheritanceBlockedAsync(targetDn, true, token);
            Require(await client.GetInheritanceBlockedAsync(targetDn, token), "Inheritance flag was not set.");
        }
        finally
        {
            foreach (var id in linked) await report.Check("cleanup.gpo-link " + id, async () => { Require(await client.DeleteLinkAsync(id, targetDn), "Created link unexpectedly absent."); });
            foreach (var id in owned.AsEnumerable().Reverse()) await report.Check("cleanup.gpo " + id, async () => { Require(await client.DeleteAsync(id), "Created GPO unexpectedly absent."); });
        }
    }
    private async Task Dns()
    {
        var client = new WindowsDnsClient(options.DnsServer ?? options.Server!);
        var zone = options.DnsZone!;
        var data = new DnsRecordData[] { new DnsRecordData.A("198.51.100.42"), new DnsRecordData.Aaaa("2001:db8::42"),
            new DnsRecordData.CName("target." + zone), new DnsRecordData.ReverseLookup("target." + zone),
            new DnsRecordData.Mx(10, "mail." + zone), new DnsRecordData.Txt("\"Runic verification\""),
            new DnsRecordData.Srv(10, 20, 12345, "target." + zone), new DnsRecordData.Ns("ns." + zone) };
        for (var index = 0; index < data.Length; index++)
        {
            var key = new DnsRecordKey(zone, "rv-" + report.Id + "-" + index + "." + zone, data[index]);
            await report.Check("dns.roundtrip." + data[index].GetType().Name, async () =>
            {
                var created = false;
                report.Resource("DNS record: " + key.OwnerName + " (" + key.Data.GetType().Name + ") on " + (options.DnsServer ?? options.Server));
                try
                {
                    Require(await client.FindZoneAsync(zone, token) is not null, "Requested fixture zone absent.");
                    Require((await client.EnumerateRecordsAsync(zone, key.OwnerName, token)).Length == 0, "Random fixture owner already exists.");
                    var serverDefaultTtl = key.Data is DnsRecordData.CName;
                    await client.CreateAsync(new(key, 300) { UseServerDefaultTimeToLive = serverDefaultTtl }, token); created = true;
                    var createdRecord = await client.FindAsync(key, token);
                    Require(createdRecord is not null, "DNS create/read mismatch.");
                    if (serverDefaultTtl) report.Resource("Server-selected DNS TTL: " + createdRecord!.TimeToLiveSeconds);
                    else Require(createdRecord!.TimeToLiveSeconds == 300, "Explicit DNS TTL mismatch.");
                    await LocalChecks.Conflict(() => client.CreateAsync(new(key, 300), token));
                    await client.UpdateAsync(key, new() { TimeToLiveSeconds = 600 }, token);
                    Require((await client.FindAsync(key, token))?.TimeToLiveSeconds == 600, "DNS TTL update missing.");
                }
                finally
                {
                    if (created) await report.Check("cleanup.dns " + key.OwnerName, async () =>
                    {
                        Require(await client.DeleteAsync(key), "Created record unexpectedly absent.");
                        Require(!await client.DeleteAsync(key), "Second DNS deletion did not report absence.");
                    });
                }
            }, token);
        }
    }
    private static string Text(DirectoryValue value) => value switch
    {
        DirectoryValue.Text text => text.Value,
        DirectoryValue.Binary binary => System.Text.Encoding.UTF8.GetString(binary.Value.AsSpan()),
        _ => throw new InvalidOperationException("Unexpected LDAP value.")
    };
}
