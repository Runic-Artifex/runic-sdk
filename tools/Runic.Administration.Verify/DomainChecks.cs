using System.Collections.Immutable;
using System.Net;
using Runic.Platform.Administration.Windows;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Dns;
using Runic.Platform.Administration.Windows.GroupPolicy;
using static RunReport;

internal sealed class DomainChecks(RunReport report, Options options, CancellationToken token)
{
    // The Default Domain Policy has this fixed GUID in every domain.
    private static readonly Guid DefaultDomainPolicy = new("31B2F340-016D-11D2-945F-00C04FB984F9");

    private WindowsDirectoryClient DirectoryClient(DirectoryTransport transport = DirectoryTransport.SignedAndSealed,
        DirectoryAuthentication authentication = DirectoryAuthentication.Negotiate, string? server = null, bool reuse = false) =>
        new(new(server ?? options.Server!) { Transport = transport, Authentication = authentication, ReuseConnection = reuse }, options.Credential);
    private WindowsDnsClient DnsClient() => new(options.DnsServer ?? options.Server!, options.Credential);
    // GPMC has no credential parameter and uses the calling thread's identity.
    private Task AsDomainIdentity(Func<Task> action) => NetworkLogon.RunAsync(options.Credential, action);

    internal async Task RunAsync()
    {
        var directory = DirectoryClient();
        var ad = new ActiveDirectoryClient(directory);
        var fixtureDn = "OU=" + report.Prefix + "," + options.BaseDn;
        var fixtureCreated = false;
        if (options.Includes("ldap"))
        {
            await report.Check("ldap.inspect", async () =>
            {
                var root = await directory.ReadRootDseAsync(["defaultNamingContext", "dnsHostName"], token);
                Require(root is not null && root.Attributes["dnsHostName"][0] is DirectoryValue.Text, "RootDSE absent or not read as text.");
                if (options.BaseDn is not null)
                    Require(await directory.FindAsync(options.BaseDn, ["distinguishedName"], token) is not null, "Explicit base DN absent.");
            }, token);
            await report.Check("domain.discover-controller", DiscoverController, token);
            foreach (var transport in new[] { DirectoryTransport.Tls, DirectoryTransport.StartTls })
                foreach (var authentication in new[] { DirectoryAuthentication.Negotiate, DirectoryAuthentication.Basic })
                {
                    var name = "ldap.transport." + transport.ToString().ToLowerInvariant() + "." + authentication.ToString().ToLowerInvariant();
                    if (authentication == DirectoryAuthentication.Basic && options.Credential is null)
                    {
                        report.Skip(name, "Basic binds need --credential-stdin.");
                        continue;
                    }
                    await report.Check(name, async () =>
                    {
                        var root = await DirectoryClient(transport, authentication).ReadRootDseAsync(["dnsHostName"], token);
                        Require(root is not null && root.Attributes.ContainsKey("dnsHostName"), "RootDSE was not read over " + transport + ".");
                    }, token);
                }
            await report.Check("ldap.tls-certificate-name-mismatch", CertificateNameMismatch, token);
            await report.Check("ldap.connection-reuse", ConnectionReuse, token);
        }
        if (options.Includes("gpo")) await report.Check("gpo.inspect", () => AsDomainIdentity(async () =>
        {
            var client = new WindowsGroupPolicyClient(options.Domain!, options.Server!);
            _ = await client.EnumerateAsync(token);
            Require(await client.FindAsync(Guid.NewGuid(), token) is null, "Random GPO ID unexpectedly found.");
        }), token);
        if (options.Includes("dns") && !options.ExpectDenied) await report.Check("dns.inspect", async () =>
        {
            var client = DnsClient();
            _ = await client.EnumerateZonesAsync(token);
            if (options.DnsZone is not null)
            {
                Require(await client.FindZoneAsync(options.DnsZone, token) is not null, "Explicit DNS zone absent.");
                _ = await client.EnumerateRecordsAsync(options.DnsZone, cancellationToken: token);
            }
        }, token);

        if (options.ExpectDenied)
        {
            await DeniedWrites(directory, ad);
            return;
        }
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
                // The mutation suite runs on one reused connection; fixture setup and cleanup bind per operation.
                if (fixtureCreated) await report.Check("ldap.objects-membership-password", async () =>
                {
                    using var reusing = DirectoryClient(reuse: true);
                    await Ldap(reusing, new ActiveDirectoryClient(reusing), fixtureDn);
                }, token);
                else report.Skip("ldap.objects-membership-password", "Fixture OU creation failed or canceled.");
            }
            if (options.Includes("gpo"))
            {
                if (fixtureCreated) await report.Check("gpo.backup-import-restore-links", () => AsDomainIdentity(() => Gpo(fixtureDn)), token);
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
    // A reusing client serves sequential and concurrent operations on one connection and refuses work once disposed.
    private async Task ConnectionReuse()
    {
        var reusing = DirectoryClient(reuse: true);
        try
        {
            var root = await reusing.ReadRootDseAsync(["defaultNamingContext", "dnsHostName"], token);
            Require(root is not null && root.Attributes["defaultNamingContext"][0] is DirectoryValue.Text, "RootDSE was not read on a reused connection.");
            var domainDn = ((DirectoryValue.Text)root!.Attributes["defaultNamingContext"][0]).Value;
            var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => reusing.FindAsync(domainDn, ["distinguishedName", "objectGUID"], token)));
            Require(reads.All(read => read is not null && read.Attributes["distinguishedName"][0] is DirectoryValue.Text && read.Attributes["objectGUID"][0] is DirectoryValue.Binary),
                "Concurrent reads on a reused connection returned wrong values.");
            if (options.BaseDn is not null)
                _ = await reusing.SearchAsync(new(options.BaseDn, "(objectClass=*)", DirectorySearchScope.Base, ["distinguishedName"]) { PageSize = 1 }, token);
            Require(await reusing.FindAsync("CN=RunicMissing-" + report.Id[..12] + "," + domainDn, ["distinguishedName"], token) is null,
                "A missing object was found on a reused connection.");
            Require((await reusing.FindAsync(domainDn, ["distinguishedName"], token)) is not null, "The reused connection failed after a missing-object result.");
        }
        finally { reusing.Dispose(); }
        try { _ = await reusing.ReadRootDseAsync(["dnsHostName"], token); throw new InvalidOperationException("A disposed client ran an operation."); }
        catch (ObjectDisposedException) { }
    }

    private async Task Ldap(WindowsDirectoryClient directory, ActiveDirectoryClient ad, string parent)
    {
        var owned = new List<string>();
        var shortId = report.Id[..12];
        var userDn = "CN=User," + parent; var groupDn = "CN=Group," + parent;
        try
        {
            var user = await ad.CreateUserAsync(parent, "User", "rvu" + shortId, cancellationToken: token); owned.Add(userDn);
            var group = await ad.CreateGroupAsync(parent, "Group", "rvg" + shortId, ActiveDirectoryGroupScope.Global, true, cancellationToken: token); owned.Add(groupDn);
            var computerDn = "CN=Computer," + parent;
            var computer = await ad.CreateComputerAsync(parent, "Computer", "rvc" + shortId + "$", token); owned.Add(computerDn);
            foreach (var dn in owned) report.Resource("AD object: " + dn);
            // The create methods return the stored DN and objectGUID of the new object.
            ActiveDirectoryObjectIdentity[] identities = [user, group, computer];
            Require(identities.Select(identity => identity.DistinguishedName).SequenceEqual(owned, StringComparer.OrdinalIgnoreCase),
                "Created object identities do not name the created DNs.");
            Require(identities.All(identity => identity.ObjectGuid != Guid.Empty) && identities.Select(identity => identity.ObjectGuid).Distinct().Count() == 3,
                "Created object identities lack distinct object GUIDs.");
            report.Resource("Created user identity: " + user.DistinguishedName + " {" + user.ObjectGuid + "}");
            var rows = await directory.SearchAsync(new(parent, "(objectClass=*)", DirectorySearchScope.OneLevel, ["distinguishedName", "objectClass"]) { PageSize = 1 }, token);
            Require(rows.Length == 3, "Paging did not return all three fixture objects.");
            await ad.AddMemberAsync(groupDn, userDn, token);
            Require((await ad.GetMembersAsync(groupDn, token)).Any(value => value.Equals(userDn, StringComparison.OrdinalIgnoreCase)), "Group membership missing.");
            await ad.RemoveMemberAsync(groupDn, userDn, token);
            Require((await ad.GetMembersAsync(groupDn, token)).Length == 0, "Group member was not removed.");
            var spn = "runic-verify/" + report.Id + "." + options.Domain;
            await ad.AddServicePrincipalNamesAsync(computerDn, [spn], token);
            Require((await ad.GetServicePrincipalNamesAsync(computerDn, token)).Any(value => value == spn), "SPN missing.");
            await ad.RemoveServicePrincipalNamesAsync(computerDn, [spn], token);
            await directory.ModifyAsync(userDn, [new("description", DirectoryModificationKind.Replace, [new DirectoryValue.Text("Grüße")])], token);
            var renamed = "CN=Renamed," + parent;
            await directory.MoveAsync(userDn, parent, "CN=Renamed", token);
            owned[0] = renamed; report.Resource("Renamed AD user: " + renamed);
            Require(await directory.FindAsync(userDn, ["distinguishedName"], token) is null, "Old DN still exists after rename.");
            var snapshot = await directory.FindAsync(renamed, ["description", "objectSid"], token);
            Require(snapshot is not null && snapshot.Attributes["objectSid"][0] is DirectoryValue.Binary, "SID was not returned as binary.");
            report.Resource("Read value types: description " + snapshot!.Attributes["description"][0].GetType().Name + ", objectSid " + snapshot.Attributes["objectSid"][0].GetType().Name);
            Require(snapshot.Attributes["description"][0] == new DirectoryValue.Text("Grüße"), "The Unicode description was not read back as text.");
            var typed = await directory.FindAsync(renamed, ["objectGUID", "whenCreated", "userAccountControl", "nTSecurityDescriptor", "distinguishedName"], token);
            Require(typed!.Attributes["objectGUID"][0] is DirectoryValue.Binary guid && new Guid(guid.Value.AsSpan()) == user.ObjectGuid,
                "The renamed user's objectGUID differs from the GUID CreateUserAsync returned.");
            Require(typed.Attributes["objectGUID"][0] is DirectoryValue.Binary && typed.Attributes["whenCreated"][0] is DirectoryValue.Text
                && typed.Attributes["userAccountControl"][0] is DirectoryValue.Text && typed.Attributes["distinguishedName"][0] is DirectoryValue.Text
                && (!typed.Attributes.TryGetValue("nTSecurityDescriptor", out var descriptor) || descriptor[0] is DirectoryValue.Binary),
                "Read value types do not follow the schema syntaxes.");
            // Generated only in memory; never included in reports or command-line arguments.
            var password = "Rv!a9-" + Guid.NewGuid().ToString("N");
            await ad.ResetPasswordAsync(renamed, password, token);
            await ad.SetAccountEnabledAsync(renamed, true, token);
            await ad.SetAccountEnabledAsync(renamed, false, token);
            await report.Check("ldap.change-password-policy", () => ad.ChangePasswordAsync(renamed, password, "Rv!b8-" + Guid.NewGuid().ToString("N"), token), token);
            await report.Check("ldap.ranged-values", () => RangedValues(directory, ad, parent), token);
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
            await report.Check("gpo.permissions-grant-remove", () => GpoPermissions(client, first.Id), token);
            await report.Check("gpo.rename", async () =>
            {
                var name = report.Prefix + "-renamed";
                await client.RenameAsync(first.Id, name, token);
                var renamed = await client.FindAsync(first.Id, token);
                Require(renamed is not null && renamed.Id == first.Id && renamed.DisplayName == name, "GPO rename was not read back.");
            }, token);
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
        var client = DnsClient();
        var zone = options.DnsZone!;
        var data = new DnsRecordData[] { new DnsRecordData.A(IPAddress.Parse("198.51.100.42")), new DnsRecordData.Aaaa(IPAddress.Parse("2001:db8::42")),
            new DnsRecordData.CName("target." + zone), new DnsRecordData.Ptr("target." + zone),
            new DnsRecordData.Mx(10, "mail." + zone), new DnsRecordData.Txt("Runic verification"),
            new DnsRecordData.Srv(10, 20, 12345, "target." + zone), new DnsRecordData.Ns("ns." + zone) };
        // Each record's data changes once in place after the TTL update.
        var changed = new DnsRecordData[] { new DnsRecordData.A(IPAddress.Parse("198.51.100.43")), new DnsRecordData.Aaaa(IPAddress.Parse("2001:db8::43")),
            new DnsRecordData.CName("target2." + zone), new DnsRecordData.Ptr("target2." + zone),
            new DnsRecordData.Mx(20, "mail2." + zone), new DnsRecordData.Txt("Runic verification 2"),
            new DnsRecordData.Srv(30, 40, 12346, "target2." + zone), new DnsRecordData.Ns("ns2." + zone) };
        for (var index = 0; index < data.Length; index++)
        {
            var key = new DnsRecordKey(zone, "rv-" + report.Id + "-" + index + "." + zone, data[index]);
            var replacement = key with { Data = changed[index] };
            await report.Check("dns.roundtrip." + data[index].GetType().Name, async () =>
            {
                var created = false;
                var current = key;
                report.Resource("DNS record: " + key.OwnerName + " (" + key.Data.GetType().Name + ") on " + (options.DnsServer ?? options.Server));
                try
                {
                    Require(await client.FindZoneAsync(zone, token) is not null, "Requested fixture zone absent.");
                    Require((await client.EnumerateRecordsAsync(zone, key.OwnerName, token)).Length == 0, "Random fixture owner already exists.");
                    var serverDefaultTtl = key.Data is DnsRecordData.CName;
                    await client.CreateAsync(new(key) { TimeToLive = serverDefaultTtl ? null : TimeSpan.FromSeconds(300) }, token); created = true;
                    var createdRecord = await client.FindAsync(key, token);
                    Require(createdRecord is not null, "DNS create/read mismatch.");
                    if (serverDefaultTtl) report.Resource("Server-selected DNS TTL: " + createdRecord!.TimeToLive);
                    else Require(createdRecord!.TimeToLive == TimeSpan.FromSeconds(300), "Explicit DNS TTL mismatch.");
                    await LocalChecks.Conflict(() => client.CreateAsync(new(key) { TimeToLive = TimeSpan.FromSeconds(300) }, token));
                    await client.UpdateAsync(key, new() { TimeToLive = TimeSpan.FromSeconds(600) }, token);
                    Require((await client.FindAsync(key, token))?.TimeToLive == TimeSpan.FromSeconds(600), "DNS TTL update missing.");
                    await client.UpdateAsync(key, new() { Data = replacement.Data }, token); current = replacement;
                    Require(await client.FindAsync(key, token) is null, "The old record data remains after an in-place data change.");
                    var updated = await client.FindAsync(replacement, token);
                    Require(updated is not null && updated.TimeToLive == TimeSpan.FromSeconds(600), "DNS data update missing or did not keep the TTL.");
                    Require((await client.EnumerateRecordsAsync(zone, key.OwnerName, token)).Length == 1, "The data change left more than one record for the owner.");
                }
                finally
                {
                    if (created) await report.Check("cleanup.dns " + key.OwnerName, async () =>
                    {
                        // A data change can fail after the server applied it; delete whichever data exists.
                        var deleted = await client.DeleteAsync(current) || await client.DeleteAsync(current == key ? replacement : key);
                        Require(deleted, "Created record unexpectedly absent.");
                        Require(!await client.DeleteAsync(current) && !await client.DeleteAsync(current == key ? replacement : key), "Second DNS deletion did not report absence.");
                    });
                }
            }, token);
        }
    }

    private async Task DiscoverController()
    {
        var client = new WindowsDomainClient();
        var membership = await client.GetMembershipAsync(token);
        Require(membership.State == DomainJoinState.Domain, "The verifier host is not joined to a domain (" + membership.State + ").");
        var controller = await client.DiscoverControllerAsync(options.Domain, cancellationToken: token);
        report.Resource("Discovered controller: " + controller.Name + " at " + controller.Address + "; domain " + controller.DomainName
            + "; forest " + controller.ForestName + "; site " + controller.ControllerSite + "; flags 0x" + controller.NativeFlags.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        Require(string.Equals(controller.DomainName.TrimEnd('.'), options.Domain!.TrimEnd('.'), StringComparison.OrdinalIgnoreCase), "Discovered a controller for another domain.");
        Require(controller.Name.StartsWith(@"\\", StringComparison.Ordinal) && controller.Address.StartsWith(@"\\", StringComparison.Ordinal), "Controller name or address lacks the native UNC prefix.");
        Require((controller.NativeFlags & 0x20000000u) != 0, "DS_DNS_CONTROLLER_FLAG missing although a DNS name was requested.");
        Require((controller.NativeFlags & 0x100u) != 0, "Writable controller requested, but DS_WRITABLE_FLAG is missing.");
        // sdk#744: the membership carries the DNS and forest names, and flat discovery the controller's NetBIOS name.
        Require(string.Equals(membership.DnsDomainName?.TrimEnd('.'), options.Domain!.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(membership.ForestName), "The membership lacks the domain's DNS or forest name: " + membership + ".");
        var flat = await client.DiscoverControllerAsync(options.Domain, false, true, DomainControllerNameFormat.Flat, token);
        report.Resource("Discovered controller (flat): " + flat.Name + "; domain " + flat.DomainName);
        Require(flat.Name.StartsWith(@"\\", StringComparison.Ordinal) && !flat.Name.Contains('.', StringComparison.Ordinal)
            && string.Equals(flat.DomainName, membership.Name, StringComparison.OrdinalIgnoreCase), "Flat discovery returned DNS names or another domain.");
    }

    // A TLS connection to the controller's IP address presents a certificate issued to its
    // DNS name. The bind must fail; certificate validation is never bypassed.
    private async Task CertificateNameMismatch()
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(options.Server!, System.Net.Sockets.AddressFamily.InterNetwork, token);
        Require(addresses.Length > 0, "The controller name has no IPv4 address.");
        var address = addresses[0].ToString();
        report.Resource("TLS name-mismatch target: " + address);
        try
        {
            _ = await DirectoryClient(DirectoryTransport.Tls, server: address).ReadRootDseAsync(["dnsHostName"], token);
        }
        catch (WindowsAdministrationException error)
        {
            report.Resource($"  rejected as expected: {error.Operation}; {error.Category}; {error.NativeErrorDomain} {error.NativeErrorCode}");
            return;
        }
        throw new InvalidOperationException("A TLS bind to " + address + " succeeded although the certificate names " + options.Server + ".");
    }

    // AD returns at most MaxValRange (1500 by default) values of a multi-valued attribute per
    // request. A group with more members than two ranges hold is read back in full. Linked
    // attributes are used because AD limits non-linked multi-valued attributes to about 1200 values.
    private async Task RangedValues(WindowsDirectoryClient directory, ActiveDirectoryClient ad, string parent)
    {
        const int count = 3100;
        var container = "OU=Range," + parent;
        var groupDn = "CN=RangeGroup," + container;
        var members = Enumerable.Range(0, count).Select(index => "CN=m" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "," + container).ToArray();
        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        var containerCreated = false;
        var groupCreated = false;
        report.Resource("Ranged fixture: " + container + " with " + groupDn + " and " + count + " contacts CN=m0000..");
        try
        {
            await ad.CreateOrganizationalUnitAsync(parent, "Range", token); containerCreated = true;
            await ad.CreateGroupAsync(container, "RangeGroup", "rvr" + report.Id[..12], ActiveDirectoryGroupScope.Global, true, cancellationToken: token); groupCreated = true;
            var contact = ImmutableDictionary<string, ImmutableArray<DirectoryValue>>.Empty.Add("objectClass", [new DirectoryValue.Text("contact")]);
            await Parallel.ForEachAsync(members, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (dn, cancellation) =>
            {
                await directory.CreateAsync(dn, contact, cancellation);
                created.Add(dn);
            });
            foreach (var batch in members.Chunk(500))
                await directory.ModifyAsync(groupDn, [new("member", DirectoryModificationKind.Add, [.. batch.Select(dn => (DirectoryValue)new DirectoryValue.Text(dn))])], token);
            var plain = await directory.FindAsync(groupDn, ["member"], token) ?? throw new InvalidOperationException("Ranged group absent.");
            var returned = plain.Attributes.Keys.Where(key => key.StartsWith("member", StringComparison.OrdinalIgnoreCase)).ToArray();
            report.Resource("Unranged read of " + count + " members returned: " + string.Join(", ", returned.Select(key => key + " (" + plain.Attributes[key].Length + ")")));
            Require(returned.All(key => plain.Attributes[key].Length < count), "The server returned every value without ranging; the check cannot exercise ranges.");
            var values = await directory.ReadAllAttributeValuesAsync(groupDn, "member", token);
            report.Resource("Ranged member value types: " + string.Join(", ", values.GroupBy(value => value.GetType().Name).Select(group => group.Key + " " + group.Count())));
            Require(values.All(value => value is DirectoryValue.Text), "Ranged member values were not read as text.");
            var actual = values.Select(Utf8).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(values.Length == count && actual.SetEquals(members), "Ranged read returned " + values.Length + " values instead of the " + count + " written.");
            Require((await ad.GetMembersAsync(groupDn, token)).Length == count, "GetMembersAsync did not return every ranged member.");
        }
        finally
        {
            await report.Check("cleanup.ranged-fixture", async () =>
            {
                var missing = 0;
                if (groupCreated && !await directory.DeleteAsync(groupDn)) missing++;
                await Parallel.ForEachAsync(created, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (dn, _) =>
                {
                    if (!await directory.DeleteAsync(dn, CancellationToken.None)) Interlocked.Increment(ref missing);
                });
                if (containerCreated && !await directory.DeleteAsync(container)) missing++;
                Require(missing == 0, missing + " created ranged-fixture objects were unexpectedly absent before cleanup.");
            });
        }
    }

    // Reads return directory values as bytes; text attributes are UTF-8.
    private static string Utf8(DirectoryValue value) => value switch
    {
        DirectoryValue.Text text => text.Value,
        DirectoryValue.Binary binary => new System.Text.UTF8Encoding(false, true).GetString(binary.Value.AsSpan()),
        _ => throw new InvalidOperationException("Unknown directory value."),
    };

    private async Task GpoPermissions(WindowsGroupPolicyClient client, Guid id)
    {
        var before = await client.GetPermissionsAsync(id, token);
        // Every GPO grants Domain Admins (RID 512); Domain Computers (RID 515) is not on a new GPO.
        var admins = before.FirstOrDefault(entry => entry.TrusteeSid.EndsWith("-512", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Domain Admins entry missing; cannot derive the domain SID.");
        var trustee = admins.TrusteeSid[..^4] + "-515";
        Require(!before.Any(entry => entry.TrusteeSid == trustee), "Domain Computers already has a permission on the new GPO.");
        report.Resource("GPO permission trustee: " + trustee + " on " + id);
        await client.GrantPermissionAsync(id, trustee, GroupPolicyPermissionLevel.Read, cancellationToken: token);
        var granted = (await client.GetPermissionsAsync(id, token)).Where(entry => entry.TrusteeSid == trustee).ToArray();
        Require(granted.Length == 1 && granted[0].NativePermission == (int)GroupPolicyPermissionLevel.Read && !granted[0].Denied && !granted[0].Inherited,
            "Granted GPO read permission was not read back.");
        await client.GrantPermissionAsync(id, trustee, GroupPolicyPermissionLevel.Edit, cancellationToken: token);
        granted = (await client.GetPermissionsAsync(id, token)).Where(entry => entry.TrusteeSid == trustee).ToArray();
        Require(granted.Length == 1 && granted[0].NativePermission == (int)GroupPolicyPermissionLevel.Edit, "GPO permission upgrade to Edit was not read back.");
        await client.RemoveTrusteePermissionsAsync(id, trustee, token);
        var after = await client.GetPermissionsAsync(id, token);
        Require(!after.Any(entry => entry.TrusteeSid == trustee), "Removed GPO trustee is still present.");
        Require(after.Length == before.Length, "Removing one trustee changed the other GPO permissions.");
    }

    // Each owned write must fail with AccessDenied. A write that unexpectedly succeeds is
    // rolled back at once, and the check still fails.
    private async Task DeniedWrites(WindowsDirectoryClient directory, ActiveDirectoryClient ad)
    {
        var baseDn = options.BaseDn!;
        if (options.Includes("ldap"))
        {
            await report.Check("ldap.denied-create", async () =>
            {
                var dn = "OU=" + report.Prefix + "," + baseDn;
                report.Resource("Denied OU attempt: " + dn);
                await Denied(() => ad.CreateOrganizationalUnitAsync(baseDn, report.Prefix, token), async () => { _ = await directory.DeleteAsync(dn); });
                Require(await directory.FindAsync(dn, ["distinguishedName"], token) is null, "Denied OU creation left an object behind.");
            }, token);
            await report.Check("ldap.denied-modify", async () =>
            {
                var before = await directory.FindAsync(baseDn, ["description"], token) ?? throw new InvalidOperationException("Base DN absent.");
                before.Attributes.TryGetValue("description", out var original);
                report.Resource("Denied description change attempt: " + baseDn);
                await Denied(() => directory.ModifyAsync(baseDn, [new("description", DirectoryModificationKind.Replace, [new DirectoryValue.Text(report.Prefix)])], token),
                    () => directory.ModifyAsync(baseDn, [new("description", DirectoryModificationKind.Replace, original.IsDefault ? [] : original)], token));
                var after = await directory.FindAsync(baseDn, ["description"], token);
                after!.Attributes.TryGetValue("description", out var current);
                // Binary values compare their arrays by reference, so compare the decoded text.
                Require((original.IsDefault ? [] : original.Select(Utf8)).SequenceEqual(current.IsDefault ? [] : current.Select(Utf8)),
                    "Denied modification changed the base DN.");
            }, token);
        }
        if (options.Includes("gpo"))
        {
            await report.Check("gpo.denied-create", () => AsDomainIdentity(async () =>
            {
                var client = new WindowsGroupPolicyClient(options.Domain!, options.Server!);
                report.Resource("Denied GPO attempt: " + report.Prefix);
                Guid? created = null;
                await Denied(async () => created = (await client.CreateAsync(report.Prefix, token)).Id,
                    async () => { if (created is { } id) _ = await client.DeleteAsync(id, token); });
                Require(!(await client.EnumerateAsync(token)).Any(gpo => gpo.DisplayName == report.Prefix), "Denied GPO creation left a GPO behind.");
            }), token);
            await report.Check("gpo.denied-link", () => AsDomainIdentity(async () =>
            {
                var client = new WindowsGroupPolicyClient(options.Domain!, options.Server!);
                report.Resource("Denied GPO link attempt: Default Domain Policy on " + baseDn);
                Require(!(await client.GetLinksAsync(baseDn, token)).Any(link => link.GroupPolicyId == DefaultDomainPolicy), "The base DN already links the Default Domain Policy.");
                await Denied(() => client.CreateLinkAsync(DefaultDomainPolicy, baseDn, cancellationToken: token),
                    async () => { _ = await client.DeleteLinkAsync(DefaultDomainPolicy, baseDn, token); });
                Require(!(await client.GetLinksAsync(baseDn, token)).Any(link => link.GroupPolicyId == DefaultDomainPolicy), "Denied link creation left a link behind.");
            }), token);
        }
        if (options.Includes("dns"))
        {
            if (options.DnsZone is null) { report.Skip("dns.denied-create", "Supply an existing disposable --dns-zone."); return; }
            await report.Check("dns.denied-create", async () =>
            {
                var client = DnsClient();
                var zone = options.DnsZone!;
                var key = new DnsRecordKey(zone, "rv-" + report.Id + "-denied." + zone, new DnsRecordData.A(IPAddress.Parse("198.51.100.43")));
                report.Resource("Denied DNS record attempt: " + key.OwnerName + " (A) on " + (options.DnsServer ?? options.Server));
                await Denied(() => client.CreateAsync(new(key) { TimeToLive = TimeSpan.FromSeconds(300) }, token), async () => { _ = await client.DeleteAsync(key, token); });
                try { Require(await client.FindAsync(key, token) is null, "Denied DNS creation left a record behind."); }
                catch (WindowsAdministrationException error) when (error.Category == AdministrationErrorCategory.AccessDenied)
                {
                    report.Resource("  record absence is not readable with this identity; verify it independently.");
                }
            }, token);
        }
    }

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
        throw new InvalidOperationException("The restricted-identity write unexpectedly succeeded; " + outcome + ".");
    }
}
