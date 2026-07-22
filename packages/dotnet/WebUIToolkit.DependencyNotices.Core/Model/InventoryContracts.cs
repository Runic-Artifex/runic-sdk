using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices;

public enum InventorySourceKind
{
    Manual,
    NuGet,
    Npm,
}

public sealed record InventoryComponent(
    PackageUrl PackageUrl,
    string Name,
    string Version,
    InventorySourceKind SourceKind,
    DependencyScope Scope,
    bool IsDirect,
    string? ObservedLicenseExpression,
    string? Integrity,
    string SourcePath,
    IReadOnlyList<NoticeEvidence> Evidence);

public sealed record InventoryResult(
    IReadOnlyList<InventoryComponent> Components,
    IReadOnlyList<NoticeDiagnostic> Diagnostics)
{
    public bool Succeeded
    {
        get
        {
            foreach (NoticeDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

public sealed class InventoryComponentComparer : IComparer<InventoryComponent>
{
    public static InventoryComponentComparer Instance { get; } = new();

    private InventoryComponentComparer()
    {
    }

    public int Compare(InventoryComponent? x, InventoryComponent? y)
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

        return System.StringComparer.Ordinal.Compare(x.PackageUrl.CanonicalValue, y.PackageUrl.CanonicalValue);
    }
}
