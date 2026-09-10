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
            using var target = StringObject(domain, 13, targetDistinguishedName, "Open GPO link target");
            using var links = Automation.GetObject(target, 13, "Read GPO links");
            var result = ImmutableArray.CreateBuilder<GroupPolicyLink>();
            var count = Automation.GetInt32(links, 7, "Read GPO link count");
            for (var i = 1; i <= count; i++) { using var link = Item(links, i); result.Add(Link(link)); }
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
            using var target = StringObject(domain, 13, targetDistinguishedName, "Open GPO link target");
            using var link = FindLink(target, id) ?? throw NativeError.Win32("Update GPO link", 2);
            if (enabled is { } enable) Automation.SetBoolean(link, 10, enable, "Set GPO link enabled state");
            if (enforced is { } enforce) Automation.SetBoolean(link, 12, enforce, "Set GPO link enforcement");
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes a direct link. Returns false if that link is absent.</summary>
    public Task<bool> DeleteLinkAsync(Guid id, string targetDistinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, 13, targetDistinguishedName, "Open GPO link target");
            using var link = FindLink(target, id);
            if (link is null) return false;
            Call(link, 15, "Delete GPO link");
            return true;
        }, cancellationToken);
    }

    /// <summary>Reads whether the target blocks inherited policy links.</summary>
    public Task<bool> GetInheritanceBlockedAsync(string targetDistinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, 13, targetDistinguishedName, "Open GPO inheritance target");
            return Automation.GetBoolean(target, 7, "Read blocked GPO inheritance");
        }, cancellationToken);
    }

    /// <summary>Changes blocked inheritance without changing direct links.</summary>
    public Task SetInheritanceBlockedAsync(string targetDistinguishedName, bool blocked, CancellationToken cancellationToken = default)
    {
        NativeError.Text(targetDistinguishedName, nameof(targetDistinguishedName));
        return Execute((_, domain) =>
        {
            using var target = StringObject(domain, 13, targetDistinguishedName, "Open GPO inheritance target");
            Automation.SetBoolean(target, 8, blocked, "Set blocked GPO inheritance");
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
            using var security = Automation.GetObject(gpo, 24, "Read GPO security");
            var result = ImmutableArray.CreateBuilder<GroupPolicyPermissionEntry>();
            var count = Automation.GetInt32(security, 7, "Read GPO permission count");
            for (var i = 1; i <= count; i++)
            {
                using var permission = Item(security, i);
                using var trustee = Automation.GetObject(permission, 11, "Read GPO trustee");
                result.Add(new(Automation.GetString(trustee, 7, "Read trustee SID"), Automation.GetString(trustee, 8, "Read trustee name"),
                    Automation.GetString(trustee, 9, "Read trustee domain"), Automation.GetInt32(permission, 10, "Read GPO permission"),
                    Automation.GetBoolean(permission, 7, "Read inherited permission"), Automation.GetBoolean(permission, 8, "Read inheritable permission"),
                    Automation.GetBoolean(permission, 9, "Read denied permission")));
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
            using var security = Automation.GetObject(gpo, 24, "Read GPO security");
            using var identity = StringObject(gpm, 13, trustee, "Resolve GPO trustee");
            SetObject(security, 12, identity.Pointer, "Remove GPO trustee permissions");
            SetObject(gpo, 25, security.Pointer, "Save GPO security");
            return true;
        }, cancellationToken);
    }

    private static string DirectoryText(DirectoryValue value) => value switch
    {
        DirectoryValue.Text text => text.Value,
        DirectoryValue.Binary binary => new UTF8Encoding(false, true).GetString(binary.Value.AsSpan()),
        _ => throw NativeError.Win32("Read GPO directory text", 13)
    };

    private static GroupPolicyLink Link(ComObject value) => new(NativeError.ParseGuid(Automation.GetString(value, 7, "Read linked GPO ID")),
        Automation.GetString(value, 8, "Read linked GPO domain"), Automation.GetInt32(value, 13, "Read GPO link order"),
        Automation.GetBoolean(value, 9, "Read GPO link enabled state"), Automation.GetBoolean(value, 11, "Read GPO link enforcement"));

    private static ComObject? FindLink(ComObject target, Guid id)
    {
        using var links = Automation.GetObject(target, 13, "Read GPO links");
        var count = Automation.GetInt32(links, 7, "Read GPO link count");
        for (var i = 1; i <= count; i++)
        {
            var link = Item(links, i);
            try { if (NativeError.ParseGuid(Automation.GetString(link, 7, "Read linked GPO ID")) == id) return link; }
            catch { link.Dispose(); throw; }
            link.Dispose();
        }
        return null;
    }

    private static unsafe void CreateLink(ComObject domain, Guid id, string targetDn, int order, bool enabled, bool enforced)
    {
        using var gpo = Required(domain, id);
        using var target = StringObject(domain, 13, targetDn, "Open GPO link target");
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)target.Slot(11))(
            target.Pointer, order, gpo.Pointer, &result), "Create GPO link");
        using var link = ComObject.Own(result);
        Automation.SetBoolean(link, 10, enabled, "Set GPO link enabled state");
        Automation.SetBoolean(link, 12, enforced, "Set GPO link enforcement");
    }

    private static unsafe void SetObject(ComObject target, int slot, nint value, string operation) =>
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)target.Slot(slot))(target.Pointer, value), operation);

    private static void SetWmiFilter(ComObject domain, Guid id, string? path)
    {
        using var gpo = Required(domain, id);
        using var filter = path is null ? null : StringObject(domain, 15, path, "Open GPO WMI filter");
        SetObject(gpo, 19, filter?.Pointer ?? 0, "Set GPO WMI filter");
    }

    private static unsafe void Grant(ComObject gpm, ComObject domain, Guid id, string trustee, GroupPolicyPermissionLevel level, bool inheritable)
    {
        using var gpo = Required(domain, id);
        using var security = Automation.GetObject(gpo, 24, "Read GPO security");
        using var identity = new BString(trustee);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, short, nint*, int>)gpm.Slot(11))(
            gpm.Pointer, identity.Pointer, (int)level, inheritable ? (short)-1 : (short)0, &result), "Create GPO permission");
        using var permission = ComObject.Own(result);
        SetObject(security, 10, permission.Pointer, "Grant GPO permission");
        SetObject(gpo, 25, security.Pointer, "Save GPO security");
    }
}
