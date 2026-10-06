using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>The Runic Views host a project references.</summary>
internal enum RunicViewsHost
{
    Unknown,
    CsWebUi,
    Desktop,
}

internal sealed record DoctorProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    string TargetFramework,
    bool IsViewsWindowProject,
    string FrontendPackageDirectory,
    string ProjectAssetsFile,
    string RuntimeIdentifier,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    bool ViteDevServerEnabled,
    RunicViewsHost Host = RunicViewsHost.Unknown)
{
    internal const string CsWebUiPackage = "Runic.Application.CsWebUi";
    internal const string DesktopPackage = "Runic.Application.Desktop";

    internal static async Task<DoctorProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken)
    {
        DevProjectConfiguration development = await DevProjectConfiguration.EvaluateAsync(
            dotnetHost, project, configuration, cancellationToken).ConfigureAwait(false);
        string properties = await ReadProjectPropertiesAsync(
            dotnetHost, development.ProjectPath, development.ProjectDirectory,
            configuration, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(properties);
        JsonElement values = document.RootElement.GetProperty("Properties");
        string Value(string name) => values.TryGetProperty(name, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        return new(
            development.ProjectPath,
            development.ProjectDirectory,
            Value("TargetFramework"),
            development.IsViewsWindowProject,
            development.FrontendPackageDirectory,
            development.ProjectAssetsFile,
            string.IsNullOrWhiteSpace(Value("RuntimeIdentifier"))
                ? Value("NETCoreSdkRuntimeIdentifier")
                : Value("RuntimeIdentifier"),
            development.ViteDevServerEntry,
            development.ViteConfigurationPath,
            development.ViteDevServerEnabled,
            DetectHost(document.RootElement));
    }

    /// <summary>
    /// Finds the Views host from the evaluated package and project references,
    /// so an unrestored project still gets host-specific checks. CS-WebUI wins
    /// when a project references both hosts because it may open a browser.
    /// </summary>
    internal static RunicViewsHost DetectHost(JsonElement evaluation)
    {
        bool csWebUi = false;
        bool desktop = false;
        if (evaluation.TryGetProperty("Items", out JsonElement items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (string itemType in new[] { "PackageReference", "ProjectReference" })
            {
                if (!items.TryGetProperty(itemType, out JsonElement references) || references.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement reference in references.EnumerateArray())
                {
                    if (!reference.TryGetProperty("Identity", out JsonElement identityNode)) continue;
                    string identity = identityNode.GetString() ?? string.Empty;
                    if (itemType == "ProjectReference")
                    {
                        identity = Path.GetFileNameWithoutExtension(identity.Replace('\\', '/').Split('/')[^1]);
                    }
                    csWebUi |= identity.Equals(CsWebUiPackage, StringComparison.OrdinalIgnoreCase) ||
                        identity.Equals("Runic.Application.Views.CsWebUi", StringComparison.OrdinalIgnoreCase);
                    desktop |= identity.Equals(DesktopPackage, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return csWebUi ? RunicViewsHost.CsWebUi : desktop ? RunicViewsHost.Desktop : RunicViewsHost.Unknown;
    }

    private static async Task<string> ReadProjectPropertiesAsync(
        string dotnetHost,
        string project,
        string projectDirectory,
        string configuration,
        CancellationToken cancellationToken)
    {
        CommandResult result = await CommandRunner.RunAsync(
            dotnetHost,
            projectDirectory,
            ["msbuild", project, "-nologo", $"-property:Configuration={configuration}",
             "-getProperty:TargetFramework,TargetFrameworks,RuntimeIdentifier,NETCoreSdkRuntimeIdentifier",
             "-getItem:PackageReference,ProjectReference"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException(
                "RAPPDEV1003",
                "MSBuild could not evaluate the project.",
                $"Project: {project}\n{result.CombinedOutput.Trim()}\n");
        }
        return result.StandardOutput;
    }
}
