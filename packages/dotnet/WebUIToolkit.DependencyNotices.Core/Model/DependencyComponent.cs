using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices;

public enum DependencyEcosystem
{
    Generic,
    NuGet,
    Npm,
}

public enum DependencyScope
{
    Runtime,
    Development,
    Optional,
    Peer,
    Bundled,
    Unknown,
}

public enum NoticeAssetKind
{
    License,
    Notice,
    Attribution,
    Authors,
    Modification,
}

public sealed record NoticeEvidence(
    NoticeAssetKind Kind,
    string Sha256,
    string Path,
    string? MediaType = null,
    string? Origin = null);

public sealed record ManualDependencyComponent(
    PackageUrl PackageUrl,
    string DisplayName,
    string Version,
    string? Revision,
    string LicenseExpression,
    IReadOnlyList<NoticeEvidence> Evidence,
    bool IsModified,
    string? ModificationNotice)
{
    public DependencyEcosystem Ecosystem => PackageUrl.Type switch
    {
        "nuget" => DependencyEcosystem.NuGet,
        "npm" => DependencyEcosystem.Npm,
        _ => DependencyEcosystem.Generic,
    };
}

public sealed class DependencyComponentComparer : IComparer<ManualDependencyComponent>
{
    public static DependencyComponentComparer Instance { get; } = new();

    private DependencyComponentComparer()
    {
    }

    public int Compare(ManualDependencyComponent? x, ManualDependencyComponent? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int byName = StringComparer.Ordinal.Compare(x.DisplayName, y.DisplayName);
        if (byName != 0)
        {
            return byName;
        }

        int byVersion = StringComparer.Ordinal.Compare(x.Version, y.Version);
        return byVersion != 0
            ? byVersion
            : StringComparer.Ordinal.Compare(x.PackageUrl.CanonicalValue, y.PackageUrl.CanonicalValue);
    }
}
