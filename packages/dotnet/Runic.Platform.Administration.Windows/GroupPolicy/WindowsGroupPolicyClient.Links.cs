using Windows.Win32.Foundation;
using Windows.Win32.System.GroupPolicy;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Administration.Windows.DirectoryServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.GroupPolicy;

public sealed partial class WindowsGroupPolicyClient
{
    /// <summary>Reads direct links in native precedence order.</summary>
    public Task<ImmutableArray<GroupPolicyLink>> GetLinksAsync(string targetDistinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, GpmLookup.Som, targetDistinguishedName, "Open GPO link target");
            using var links = GpmRead.GetObject(target, GpmGetObject.SOMGetGPOLinks, "Read GPO links");
            var result = ImmutableArray.CreateBuilder<GroupPolicyLink>();
            var count = GpmRead.GetInt32(links, GpmGetInt32.GPOLinksCollectionCount, "Read GPO link count");
            for (var i = 1; i <= count; i++) { using var link = Item(links, GpmCollection.Links, i); result.Add(Link(link)); }
            return result.OrderBy(link => link.Order).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>Creates a link at a one-based position; -1 appends at lowest precedence. An existing link is a conflict.</summary>
    public Task CreateLinkAsync(Guid id, string targetDistinguishedName, int order = -1, bool enabled = true, bool enforced = false,
        CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        if (order == 0 || order < -1) throw new ArgumentOutOfRangeException(nameof(order));
        return Execute((_, domain) => { CreateLink(domain, id, targetDistinguishedName, order, enabled, enforced); return true; }, cancellationToken);
    }

    /// <summary>Updates selected direct-link flags. Separate native changes are not transactional.</summary>
    public Task UpdateLinkAsync(Guid id, string targetDistinguishedName, bool? enabled = null, bool? enforced = null,
        CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, GpmLookup.Som, targetDistinguishedName, "Open GPO link target");
            using var link = FindLink(target, id) ?? throw NativeError.Win32("Update GPO link", 2);
            if (enabled is { } enable) GpmRead.SetBoolean(link, GpmSetBoolean.GPOLinkEnabled, enable, "Set GPO link enabled state");
            if (enforced is { } enforce) GpmRead.SetBoolean(link, GpmSetBoolean.GPOLinkEnforced, enforce, "Set GPO link enforcement");
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes a direct link. Returns false if that link is absent.</summary>
    public Task<bool> DeleteLinkAsync(Guid id, string targetDistinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, GpmLookup.Som, targetDistinguishedName, "Open GPO link target");
            using var link = FindLink(target, id);
            if (link is null) return false;
            DeleteObject(link, true, "Delete GPO link");
            return true;
        }, cancellationToken);
    }

    /// <summary>Reads whether the target blocks inherited policy links.</summary>
    public Task<bool> GetInheritanceBlockedAsync(string targetDistinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, GpmLookup.Som, targetDistinguishedName, "Open GPO inheritance target");
            return GpmRead.GetBoolean(target, GpmGetBoolean.SOMGPOInheritanceBlocked, "Read blocked GPO inheritance");
        }, cancellationToken);
    }

    /// <summary>Changes blocked inheritance without changing direct links.</summary>
    public Task SetInheritanceBlockedAsync(string targetDistinguishedName, bool blocked, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, GpmLookup.Som, targetDistinguishedName, "Open GPO inheritance target");
            GpmRead.SetBoolean(target, GpmSetBoolean.SOMGPOInheritanceBlocked, blocked, "Set blocked GPO inheritance");
            return true;
        }, cancellationToken);
    }

    /// <summary>Reorders every direct link, highest precedence first. Retains exact link text/options and rejects concurrent changes.</summary>
    public async Task SetLinkOrderAsync(string targetDistinguishedName, ImmutableArray<Guid> highestPrecedenceFirst, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        if (highestPrecedenceFirst.IsDefault || highestPrecedenceFirst.Any(id => id == Guid.Empty) ||
            highestPrecedenceFirst.Distinct().Count() != highestPrecedenceFirst.Length)
            throw new ArgumentException("Supply each linked GPO GUID exactly once.", nameof(highestPrecedenceFirst));
        var directory = new WindowsDirectoryClient(new(_controller));
        var target = await directory.FindAsync(targetDistinguishedName, ["gPLink"], cancellationToken).ConfigureAwait(false)
            ?? throw NativeError.Win32("Read GPO link target", 2);
        var original = target.Attributes.TryGetValue("gPLink", out var values) && values.Length == 1 ? DirectoryText(values[0]) : "";
        var nativeOrder = await GetLinksAsync(targetDistinguishedName, cancellationToken).ConfigureAwait(false);
        var replacement = ReorderLinks(original, nativeOrder.Select(link => link.GroupPolicyId).ToImmutableArray(), highestPrecedenceFirst);
        if (replacement == original) return;
        await directory.CompareAndReplaceTextAsync(targetDistinguishedName, "gPLink", original, replacement, cancellationToken).ConfigureAwait(false);
    }

    internal static string ReorderLinks(string original, ImmutableArray<Guid> nativeOrder, ImmutableArray<Guid> requestedOrder)
    {
        var entries = new List<(Guid Id, string Text)>();
        var offset = 0;
        while (offset < original.Length)
        {
            if (original[offset] != '[') throw NativeError.Win32("Parse GPO link list", 13);
            var end = original.IndexOf(']', offset + 1);
            if (end < 0) throw NativeError.Win32("Parse GPO link list", 13);
            var entry = original.Substring(offset, end - offset + 1);
            var start = entry.IndexOf("CN={", StringComparison.OrdinalIgnoreCase);
            var close = start < 0 ? -1 : entry.IndexOf('}', start + 4);
            if (start < 0 || close < 0 || !Guid.TryParse(entry.AsSpan(start + 3, close - start - 2), out var id))
                throw NativeError.Win32("Parse linked GPO identity", 13);
            entries.Add((id, entry));
            offset = end + 1;
        }
        if (entries.Count != requestedOrder.Length || entries.Count != nativeOrder.Length ||
            entries.Select(entry => entry.Id).Distinct().Count() != entries.Count ||
            !entries.Select(entry => entry.Id).ToHashSet().SetEquals(requestedOrder))
            throw NativeError.Win32("Match complete GPO link order", 183);
        var storedOrder = entries.Select(entry => entry.Id).ToArray();
        var sameDirection = storedOrder.SequenceEqual(nativeOrder);
        if (!sameDirection && !storedOrder.SequenceEqual(nativeOrder.Reverse()))
            throw NativeError.Win32("Match observed GPO precedence", 183);
        var ordered = sameDirection ? requestedOrder.AsEnumerable() : requestedOrder.Reverse();
        var byId = entries.ToDictionary(entry => entry.Id, entry => entry.Text);
        return string.Concat(ordered.Select(id => byId[id]));
    }

    /// <summary>Associates an explicit WMI-filter directory path; null removes the association.</summary>
    public Task SetWmiFilterAsync(Guid id, string? filterPath, CancellationToken cancellationToken = default)
    {
        if (filterPath is not null) NativeError.Text(filterPath, nameof(filterPath));
        return Execute((_, domain) => { SetWmiFilter(domain, id, filterPath); return true; }, cancellationToken);
    }

    /// <summary>Reads security filtering/delegation including inherited and custom permission entries.</summary>
    public Task<ImmutableArray<GroupPolicyPermissionEntry>> GetPermissionsAsync(Guid id, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Required(domain, id);
            using var security = GpmRead.GetObject(gpo, GpmGetObject.GPOGetSecurityInfo, "Read GPO security");
            var result = ImmutableArray.CreateBuilder<GroupPolicyPermissionEntry>();
            var count = GpmRead.GetInt32(security, GpmGetInt32.SecurityInfoCount, "Read GPO permission count");
            for (var i = 1; i <= count; i++)
            {
                using var permission = Item(security, GpmCollection.Permissions, i);
                using var trustee = GpmRead.GetObject(permission, GpmGetObject.PermissionTrustee, "Read GPO trustee");
                result.Add(new(GpmRead.GetString(trustee, GpmGetString.TrusteeTrusteeSid, "Read trustee SID"), GpmRead.GetString(trustee, GpmGetString.TrusteeTrusteeName, "Read trustee name"),
                    GpmRead.GetString(trustee, GpmGetString.TrusteeTrusteeDomain, "Read trustee domain"), GpmRead.GetInt32(permission, GpmGetInt32.PermissionPermission, "Read GPO permission"),
                    GpmRead.GetBoolean(permission, GpmGetBoolean.PermissionInherited, "Read inherited permission"), GpmRead.GetBoolean(permission, GpmGetBoolean.PermissionInheritable, "Read inheritable permission"),
                    GpmRead.GetBoolean(permission, GpmGetBoolean.PermissionDenied, "Read denied permission")));
            }
            return result.ToImmutable();
        }, cancellationToken);

    /// <summary>Grants a curated permission to an explicit trustee name or SID, retaining other trustees.</summary>
    public Task GrantPermissionAsync(Guid id, string trustee, GroupPolicyPermissionLevel level, bool inheritable = false, CancellationToken cancellationToken = default)
    {
        NativeError.Text(trustee, nameof(trustee));
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        return Execute((gpm, domain) => { Grant(gpm, domain, id, trustee, level, inheritable); return true; }, cancellationToken);
    }

    /// <summary>Removes explicit permissions for one trustee. Inherited permissions remain governed by their parent.</summary>
    public Task RemoveTrusteePermissionsAsync(Guid id, string trustee, CancellationToken cancellationToken = default)
    {
        NativeError.Text(trustee, nameof(trustee));
        return Execute((gpm, domain) =>
        {
            using var gpo = Required(domain, id);
            using var security = GpmRead.GetObject(gpo, GpmGetObject.GPOGetSecurityInfo, "Read GPO security");
            RemoveTrustee(security, trustee);
            SetObject(gpo, GpmObjectField.Security, security.Pointer, "Save GPO security");
            return true;
        }, cancellationToken);
    }

    private static string DirectoryText(DirectoryValue value) => value switch
    {
        DirectoryValue.Text text => text.Value,
        DirectoryValue.Binary binary => new UTF8Encoding(false, true).GetString(binary.Value.AsSpan()),
        _ => throw NativeError.Win32("Read GPO directory text", 13)
    };

    private static GroupPolicyLink Link(ComObject value) => new(NativeError.ParseGuid(GpmRead.GetString(value, GpmGetString.GPOLinkGPOID, "Read linked GPO ID")),
        GpmRead.GetString(value, GpmGetString.GPOLinkGPODomain, "Read linked GPO domain"), GpmRead.GetInt32(value, GpmGetInt32.GPOLinkSOMLinkOrder, "Read GPO link order"),
        GpmRead.GetBoolean(value, GpmGetBoolean.GPOLinkEnabled, "Read GPO link enabled state"), GpmRead.GetBoolean(value, GpmGetBoolean.GPOLinkEnforced, "Read GPO link enforcement"));

    private static ComObject? FindLink(ComObject target, Guid id)
    {
        using var links = GpmRead.GetObject(target, GpmGetObject.SOMGetGPOLinks, "Read GPO links");
        var count = GpmRead.GetInt32(links, GpmGetInt32.GPOLinksCollectionCount, "Read GPO link count");
        for (var i = 1; i <= count; i++)
        {
            var link = Item(links, GpmCollection.Links, i);
            try { if (NativeError.ParseGuid(GpmRead.GetString(link, GpmGetString.GPOLinkGPOID, "Read linked GPO ID")) == id) return link; }
            catch { link.Dispose(); throw; }
            link.Dispose();
        }
        return null;
    }

    private static unsafe void CreateLink(ComObject domain, Guid id, string targetDn, int order, bool enabled, bool enforced)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var gpo = Required(domain, id);
        using var target = StringObject(domain, GpmLookup.Som, targetDn, "Open GPO link target");
        nint result = 0;
        using var link = ComObject.FromResult(((IGPMSOM*)target.Pointer)->CreateGPOLink(order, (IGPMGPO*)gpo.Pointer, (IGPMGPOLink**)&result).Value, result, "Create GPO link");
        GpmRead.SetBoolean(link, GpmSetBoolean.GPOLinkEnabled, enabled, "Set GPO link enabled state");
        GpmRead.SetBoolean(link, GpmSetBoolean.GPOLinkEnforced, enforced, "Set GPO link enforcement");
    }

    private enum GpmObjectField { Security, WmiFilter, Permission }
    private static unsafe void SetObject(ComObject target, GpmObjectField field, nint value, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        var status = field switch
        {
            GpmObjectField.Security => ((IGPMGPO*)target.Pointer)->SetSecurityInfo((IGPMSecurityInfo*)value),
            GpmObjectField.WmiFilter => ((IGPMGPO*)target.Pointer)->SetWMIFilter((IGPMWMIFilter*)value),
            GpmObjectField.Permission => ((IGPMSecurityInfo*)target.Pointer)->Add((IGPMPermission*)value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
    }

    private static unsafe void RemoveTrustee(ComObject security, string trustee)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var name = new BString(trustee);
        NativeError.Check(((IGPMSecurityInfo*)security.Pointer)->RemoveTrustee(name.Native).Value, "Remove GPO trustee permissions");
    }

    private static void SetWmiFilter(ComObject domain, Guid id, string? path)
    {
        using var gpo = Required(domain, id);
        using var filter = path is null ? null : StringObject(domain, GpmLookup.WmiFilter, path, "Open GPO WMI filter");
        SetObject(gpo, GpmObjectField.WmiFilter, filter?.Pointer ?? 0, "Set GPO WMI filter");
    }

    private static unsafe void Grant(ComObject gpm, ComObject domain, Guid id, string trustee, GroupPolicyPermissionLevel level, bool inheritable)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var gpo = Required(domain, id);
        using var security = GpmRead.GetObject(gpo, GpmGetObject.GPOGetSecurityInfo, "Read GPO security");
        using var identity = new BString(trustee);
        nint result = 0;
        using var permission = ComObject.FromResult(((IGPM*)gpm.Pointer)->CreatePermission(identity.Native, (GPMPermissionType)level, new VARIANT_BOOL(inheritable ? (short)-1 : (short)0), (IGPMPermission**)&result).Value, result, "Create GPO permission");
        SetObject(security, GpmObjectField.Permission, permission.Pointer, "Grant GPO permission");
        SetObject(gpo, GpmObjectField.Security, security.Pointer, "Save GPO security");
    }
}
