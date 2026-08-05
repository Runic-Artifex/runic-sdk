using System;
using System.Collections.Generic;

namespace RunicToolkit.Hosting.Build;

/// <summary>Identifies a stable frontend asset manifest validation condition.</summary>
public enum FrontendAssetManifestIssueKind
{
    /// <summary>The manifest version is not supported.</summary>
    UnsupportedVersion,
    /// <summary>The manifest does not contain assets.</summary>
    EmptyManifest,
    /// <summary>An asset entry is null.</summary>
    NullAsset,
    /// <summary>Paths differ only by case.</summary>
    DuplicatePath,
    /// <summary>Assets are not sorted by normalized path using ordinal comparison.</summary>
    NonDeterministicOrder,
    /// <summary>The manifest does not declare exactly one entry point.</summary>
    EntryPointCardinality,
    /// <summary>A declared compressed variant is absent from the manifest asset set.</summary>
    MissingCompressedVariant,
}

/// <summary>Describes one stable, content-safe manifest validation issue.</summary>
/// <param name="Kind">The validation condition.</param>
/// <param name="AssetIndex">The zero-based asset index, or <see langword="null" /> for a manifest-level issue.</param>
/// <param name="Message">A stable message that contains no asset path or content.</param>
public sealed record FrontendAssetManifestIssue(
    FrontendAssetManifestIssueKind Kind,
    int? AssetIndex,
    string Message);

/// <summary>Validates the deterministic invariants required by frontend asset consumers.</summary>
public static class FrontendAssetManifestValidator
{
    /// <summary>Validates a manifest and returns issues in deterministic detection order.</summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <returns>An immutable snapshot of validation issues.</returns>
    public static IReadOnlyList<FrontendAssetManifestIssue> Validate(IFrontendAssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var issues = new List<FrontendAssetManifestIssue>();
        if (!StringComparer.Ordinal.Equals(manifest.ManifestVersion, FrontendAssetManifest.CurrentVersion))
        {
            issues.Add(new(
                FrontendAssetManifestIssueKind.UnsupportedVersion,
                null,
                "The frontend asset manifest version is not supported."));
        }

        IReadOnlyList<FrontendAsset>? assets = manifest.Assets;
        if (assets is null || assets.Count == 0)
        {
            issues.Add(new(
                FrontendAssetManifestIssueKind.EmptyManifest,
                null,
                "The frontend asset manifest must contain at least one asset."));
            issues.Add(new(
                FrontendAssetManifestIssueKind.EntryPointCardinality,
                null,
                "The frontend asset manifest must contain exactly one entry point."));
            return issues.AsReadOnly();
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryPointCount = 0;
        string? previousPath = null;

        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (asset is null)
            {
                issues.Add(new(
                    FrontendAssetManifestIssueKind.NullAsset,
                    index,
                    "The frontend asset manifest cannot contain null entries."));
                continue;
            }

            if (!paths.Add(asset.RelativePath))
            {
                issues.Add(new(
                    FrontendAssetManifestIssueKind.DuplicatePath,
                    index,
                    "Frontend asset paths must be unique using case-insensitive comparison."));
            }

            if (previousPath is not null
                && StringComparer.Ordinal.Compare(previousPath, asset.RelativePath) >= 0)
            {
                issues.Add(new(
                    FrontendAssetManifestIssueKind.NonDeterministicOrder,
                    index,
                    "Frontend assets must be sorted by normalized path using ordinal comparison."));
            }

            previousPath = asset.RelativePath;
            if (asset.IsEntryPoint)
            {
                entryPointCount++;
            }
        }

        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (asset is null)
            {
                continue;
            }

            if (asset.BrotliPath is not null && !paths.Contains(asset.BrotliPath))
            {
                issues.Add(new(
                    FrontendAssetManifestIssueKind.MissingCompressedVariant,
                    index,
                    "A declared compressed variant must refer to a present manifest asset."));
            }

            if (asset.GzipPath is not null && !paths.Contains(asset.GzipPath))
            {
                issues.Add(new(
                    FrontendAssetManifestIssueKind.MissingCompressedVariant,
                    index,
                    "A declared compressed variant must refer to a present manifest asset."));
            }
        }

        if (entryPointCount != 1)
        {
            issues.Add(new(
                FrontendAssetManifestIssueKind.EntryPointCardinality,
                null,
                "The frontend asset manifest must contain exactly one entry point."));
        }

        return issues.AsReadOnly();
    }
}
