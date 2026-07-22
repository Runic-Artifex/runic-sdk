using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class ThirdPartyNoticesTextRenderer
{
    public static byte[] Render(DependencyNoticeDocument document)
    {
        StringBuilder builder = new();
        _ = builder.Append("THIRD-PARTY NOTICES\n")
            .Append("Artifact: ").Append(document.ArtifactName).Append('\n');
        if (document.ArtifactVersion is not null)
        {
            _ = builder.Append("Version: ").Append(document.ArtifactVersion).Append('\n');
        }

        List<DependencyNotice> dependencies = NoticeOrdering.Dependencies(document.Dependencies);
        _ = builder.Append("Components: ").Append(dependencies.Count.ToString(CultureInfo.InvariantCulture)).Append("\n\n");

        SortedDictionary<string, NoticeAsset> evidence = new(System.StringComparer.Ordinal);
        foreach (DependencyNotice dependency in dependencies)
        {
            _ = builder.Append("================================================================\n")
                .Append(dependency.Name).Append(' ').Append(dependency.Version).Append('\n')
                .Append("Package URL: ").Append(dependency.PackageUrl).Append('\n')
                .Append("License (observed): ").Append(dependency.ObservedLicenseExpression).Append('\n')
                .Append("License (effective): ").Append(dependency.EffectiveLicenseExpression).Append('\n');
            if (dependency.SelectedLicenseExpression is not null)
            {
                _ = builder.Append("License (selected): ").Append(dependency.SelectedLicenseExpression).Append('\n');
            }

            _ = builder.Append("Scope: ").Append(RenderingUtilities.EnumToken(dependency.Scope)).Append('\n')
                .Append("Relationship: ").Append(dependency.IsDirect ? "direct" : "transitive").Append('\n');
            if (dependency.IsModified && dependency.ModificationNotice is not null)
            {
                _ = builder.Append("Modification: ").Append(dependency.ModificationNotice).Append('\n');
            }

            foreach (NoticeAsset asset in NoticeOrdering.Assets(dependency.Assets))
            {
                _ = builder.Append("Evidence: ")
                    .Append(RenderingUtilities.EnumToken(asset.Kind))
                    .Append(" sha256:").Append(asset.Sha256)
                    .Append(" Origin: ").Append(asset.Origin).Append('\n');
                if (!evidence.ContainsKey(asset.Sha256))
                {
                    evidence.Add(asset.Sha256, asset);
                }
            }

            _ = builder.Append('\n');
        }

        _ = builder.Append("EVIDENCE APPENDIX\n");
        foreach (KeyValuePair<string, NoticeAsset> pair in evidence)
        {
            _ = builder.Append("----------------------------------------------------------------\n")
                .Append("BEGIN EVIDENCE sha256:").Append(pair.Key).Append('\n')
                .Append("Kind: ").Append(RenderingUtilities.EnumToken(pair.Value.Kind)).Append('\n')
                .Append("Media type: ").Append(pair.Value.MediaType).Append('\n')
                .Append("Origin: ").Append(pair.Value.Origin).Append("\n\n")
                .Append(pair.Value.Text);
            if (!pair.Value.Text.EndsWith('\n'))
            {
                _ = builder.Append('\n');
            }

            _ = builder.Append("END EVIDENCE sha256:").Append(pair.Key).Append("\n\n");
        }

        if (builder.Length >= 2 && builder[^1] == '\n' && builder[^2] == '\n')
        {
            builder.Length--;
        }

        return RenderingUtilities.Utf8NoBom.GetBytes(builder.ToString());
    }
}
