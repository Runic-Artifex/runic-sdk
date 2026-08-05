using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace RunicToolkit.Hosting.Build;

/// <summary>
/// Deterministically generates or verifies a frontend manifest from one explicit output directory.
/// </summary>
public sealed class GenerateFrontendAssetManifestTask : Microsoft.Build.Utilities.Task
{
    /// <summary>Gets or sets the frontend output directory.</summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the sole application-relative entry point.</summary>
    [Required]
    public string EntryPoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the manifest output path.</summary>
    [Required]
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the task verifies existing bytes instead of updating them.
    /// </summary>
    public bool VerifyOnly { get; set; }

    /// <summary>Gets the deterministic asset items emitted by the task.</summary>
    [Output]
    public ITaskItem[] Assets { get; private set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            var builder = new FrontendAssetManifestBuilder();
            string outputRoot = Path.GetFullPath(OutputDirectory);
            string manifestFullPath = Path.GetFullPath(ManifestPath);
            string manifestRelativePath = Path.GetRelativePath(outputRoot, manifestFullPath);
            string[] exclusions = !Path.IsPathRooted(manifestRelativePath)
                && manifestRelativePath != ".."
                && !manifestRelativePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                    ? [manifestRelativePath]
                    : [];
            FrontendAssetManifest manifest = builder.BuildFromDirectory(
                OutputDirectory,
                EntryPoint,
                exclusions);
            byte[] expected = FrontendAssetManifestJson.SerializeToUtf8Bytes(manifest);
            if (VerifyOnly)
            {
                if (!File.Exists(manifestFullPath)
                    || !File.ReadAllBytes(manifestFullPath).AsSpan().SequenceEqual(expected))
                {
                    Log.LogError(
                        "RTKHOST0006: The frontend asset manifest is missing or stale.");
                    return false;
                }
            }
            else
            {
                WriteIfChanged(manifestFullPath, expected);
            }

            Assets = manifest.Assets
                .Select(asset => CreateTaskItem(outputRoot, asset))
                .ToArray();
            return !Log.HasLoggedErrors;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            Log.LogError(
                "RTKHOST0006: Frontend asset manifest generation failed: {0}",
                SafeExceptionKind(exception));
            return false;
        }
    }

    private static TaskItem CreateTaskItem(string outputRoot, FrontendAsset asset)
    {
        var item = new TaskItem(Path.Combine(
            outputRoot,
            asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        item.SetMetadata("RelativePath", asset.RelativePath);
        item.SetMetadata("MediaType", asset.MediaType);
        item.SetMetadata("Sha256", asset.Sha256);
        item.SetMetadata("IsEntryPoint", asset.IsEntryPoint ? "true" : "false");
        return item;
    }

    private static void WriteIfChanged(string manifestPath, byte[] content)
    {
        if (File.Exists(manifestPath)
            && File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(content))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = manifestPath + ".tmp";
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static string SafeExceptionKind(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "directory-not-found",
        FileNotFoundException => "file-not-found",
        UnauthorizedAccessException => "access-denied",
        InvalidOperationException => "invalid-manifest",
        ArgumentException => "invalid-input",
        _ => "io-failure",
    };
}
