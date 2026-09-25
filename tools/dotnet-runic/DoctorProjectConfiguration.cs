using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

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
    bool ViteDevServerEnabled)
{
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
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(properties);
        System.Text.Json.JsonElement values = document.RootElement.GetProperty("Properties");
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
            development.ViteDevServerEnabled);
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
             "-getProperty:TargetFramework,TargetFrameworks,RuntimeIdentifier,NETCoreSdkRuntimeIdentifier"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException("RAPPDEV1003", $"Could not evaluate '{project}'. {result.CombinedOutput.Trim()}");
        }
        return result.StandardOutput;
    }
}
