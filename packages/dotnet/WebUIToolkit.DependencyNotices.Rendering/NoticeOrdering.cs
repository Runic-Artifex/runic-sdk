using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Rendering;

internal static class NoticeOrdering
{
    internal static List<DependencyNotice> Dependencies(IReadOnlyList<DependencyNotice> source)
    {
        List<DependencyNotice> result = new(source);
        result.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Name.Normalize(), right.Name.Normalize());
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Version, right.Version);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl);
        });
        return result;
    }

    internal static List<NoticeAsset> Assets(IReadOnlyList<NoticeAsset> source)
    {
        List<NoticeAsset> result = new(source);
        result.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(RenderingUtilities.EnumToken(left.Kind), RenderingUtilities.EnumToken(right.Kind));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Sha256, right.Sha256);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Origin, right.Origin);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.MediaType, right.MediaType);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Text, right.Text);
            return comparison != 0 ? comparison : left.IsOverride.CompareTo(right.IsOverride);
        });
        return result;
    }

    internal static List<NoticePolicyDecision> Decisions(IReadOnlyList<NoticePolicyDecision> source)
    {
        List<NoticePolicyDecision> result = new(source);
        result.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Subject, right.Subject);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(RenderingUtilities.EnumToken(left.Outcome), RenderingUtilities.EnumToken(right.Outcome));
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Rule, right.Rule);
        });
        return result;
    }

    internal static List<NoticeDiagnostic> Diagnostics(IReadOnlyList<NoticeDiagnostic> source)
    {
        List<NoticeDiagnostic> result = new(source);
        result.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Code, right.Code);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Source, right.Source);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Message, right.Message);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(RenderingUtilities.EnumToken(left.Severity), RenderingUtilities.EnumToken(right.Severity));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Nullable.Compare(left.Offset, right.Offset);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Remediation, right.Remediation);
        });
        return result;
    }
}
