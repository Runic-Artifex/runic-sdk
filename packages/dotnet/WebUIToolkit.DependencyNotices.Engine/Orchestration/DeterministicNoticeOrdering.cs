using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Engine;

internal sealed class InventoryMergeComparer : IComparer<InventoryComponent>
{
    public static InventoryMergeComparer Instance { get; } = new();

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

        int result = StringComparer.Ordinal.Compare(
            x.PackageUrl.CanonicalValue,
            y.PackageUrl.CanonicalValue);
        if (result != 0)
        {
            return result;
        }

        result = x.SourceKind.CompareTo(y.SourceKind);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(x.SourcePath, y.SourcePath);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(x.Name, y.Name);
        return result != 0 ? result : StringComparer.Ordinal.Compare(x.Version, y.Version);
    }
}

internal sealed class DiagnosticComparer : IComparer<NoticeDiagnostic>
{
    public static DiagnosticComparer Instance { get; } = new();

    public int Compare(NoticeDiagnostic? x, NoticeDiagnostic? y)
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

        int result = StringComparer.Ordinal.Compare(x.Code, y.Code);
        result = result != 0 ? result : StringComparer.Ordinal.Compare(x.PackageUrl, y.PackageUrl);
        result = result != 0 ? result : StringComparer.Ordinal.Compare(x.Source, y.Source);
        result = result != 0 ? result : Nullable.Compare(x.Offset, y.Offset);
        result = result != 0 ? result : x.Severity.CompareTo(y.Severity);
        return result != 0 ? result : StringComparer.Ordinal.Compare(x.Message, y.Message);
    }
}

internal sealed class RenderedOutputComparer : IComparer<RenderedNoticeOutput>
{
    public static RenderedOutputComparer Instance { get; } = new();

    public int Compare(RenderedNoticeOutput? x, RenderedNoticeOutput? y) =>
        StringComparer.Ordinal.Compare(x?.RelativePath, y?.RelativePath);
}
