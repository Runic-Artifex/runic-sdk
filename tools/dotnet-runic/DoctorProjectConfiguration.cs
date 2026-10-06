using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal const string DesktopGtk4Package = "Runic.Desktop.Gtk4";

    /// <summary>The semicolon-separated RuntimeIdentifiers property.</summary>
    internal string RuntimeIdentifiers { get; init; } = string.Empty;

    /// <summary>PublishAot, evaluated for the doctor target RID when one is given.</summary>
    internal bool PublishAot { get; init; }

    /// <summary>SelfContained, evaluated for the doctor target RID when one is given.</summary>
    internal bool SelfContained { get; init; }

    /// <summary>False when StripSymbols is explicitly false; Native AOT then needs no objcopy.</summary>
    internal bool StripSymbols { get; init; } = true;

    /// <summary>True when the project references the optional GTK 4 Desktop provider.</summary>
    internal bool UsesGtk4 { get; init; }

    /// <summary>
    /// The global properties 'dotnet publish -r rid' sets, plus PublishAot and
    /// SelfContained for the --aot and --self-contained switches.
    /// </summary>
    internal static IReadOnlyList<string> PublishProperties(DoctorTargetRid target, bool aot, bool selfContained)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<string> properties = [$"RuntimeIdentifier={target.Value}", "_IsPublishing=true"];
        if (aot) properties.Add("PublishAot=true");
        if (selfContained) properties.AddRange(["SelfContained=true", "_CommandLineDefinedSelfContained=true"]);
        return properties;
    }

    internal static async Task<DoctorProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? publishProperties = null)
    {
        DevProjectConfiguration development = await DevProjectConfiguration.EvaluateAsync(
            dotnetHost, project, configuration, cancellationToken).ConfigureAwait(false);
        string properties = await ReadProjectPropertiesAsync(
            dotnetHost, development.ProjectPath, development.ProjectDirectory,
            configuration, publishProperties ?? [], cancellationToken).ConfigureAwait(false);
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
            DetectHost(document.RootElement))
        {
            RuntimeIdentifiers = Value("RuntimeIdentifiers"),
            PublishAot = IsTrue(Value("PublishAot")),
            SelfContained = IsTrue(Value("SelfContained")),
            StripSymbols = !Value("StripSymbols").Equals("false", StringComparison.OrdinalIgnoreCase),
            UsesGtk4 = References(document.RootElement, DesktopGtk4Package),
        };
    }

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the Views host from the evaluated package and project references,
    /// so an unrestored project still gets host-specific checks. CS-WebUI wins
    /// when a project references both hosts because it may open a browser.
    /// </summary>
    internal static RunicViewsHost DetectHost(JsonElement evaluation)
    {
        bool csWebUi = References(evaluation, CsWebUiPackage) || References(evaluation, "Runic.Application.Views.CsWebUi");
        bool desktop = References(evaluation, DesktopPackage);
        return csWebUi ? RunicViewsHost.CsWebUi : desktop ? RunicViewsHost.Desktop : RunicViewsHost.Unknown;
    }

    /// <summary>Whether the evaluation has a package or project reference to <paramref name="name"/>.</summary>
    internal static bool References(JsonElement evaluation, string name)
    {
        if (!evaluation.TryGetProperty("Items", out JsonElement items) || items.ValueKind != JsonValueKind.Object) return false;
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
                if (identity.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static async Task<string> ReadProjectPropertiesAsync(
        string dotnetHost,
        string project,
        string projectDirectory,
        string configuration,
        IReadOnlyList<string> publishProperties,
        CancellationToken cancellationToken)
    {
        // For --rid, the global properties of 'dotnet publish -r' make RID- and
        // publish-conditioned PublishAot and SelfContained settings apply.
        List<string> arguments =
        [
            "msbuild", project, "-nologo", $"-property:Configuration={configuration}",
            "-getProperty:TargetFramework,TargetFrameworks,RuntimeIdentifier,RuntimeIdentifiers,NETCoreSdkRuntimeIdentifier,PublishAot,SelfContained,StripSymbols",
            "-getItem:PackageReference,ProjectReference",
        ];
        arguments.AddRange(publishProperties.Select(static property => $"-property:{property}"));
        CommandResult result = await CommandRunner.RunAsync(
            dotnetHost,
            projectDirectory,
            arguments,
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
