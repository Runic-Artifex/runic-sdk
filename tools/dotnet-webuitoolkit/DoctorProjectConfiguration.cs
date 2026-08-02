using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed record DoctorProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    string TargetFramework,
    bool FrontendEnabled,
    bool NodeEnabled,
    bool CwhtmlEnabled,
    string WorkspaceRoot,
    string Workspace,
    string FrontendPackageDirectory,
    string ContractSource,
    string ContractCSharpOutput,
    string ContractTypeScriptOutput,
    string ContractTool,
    bool ViteDevServerEnabled,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    string ProjectAssetsFile,
    string RuntimeIdentifier)
{
    internal bool CsharpMarkupEnabled { get; init; }

    internal string CsharpMarkupGeneratorAssembly { get; init; } = string.Empty;

    internal string CsharpMarkupManifestPath { get; init; } = string.Empty;

    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "TargetFramework",
        "TargetFrameworks",
        "WebUIToolkitFrontendEnabled",
        "WebUIToolkitFrontendNodeEnabled",
        "WebUIToolkitFrontendCwhtmlEnabled",
        "WebUIToolkitFrontendWorkspaceRoot",
        "WebUIToolkitFrontendWorkspace",
        "WebUIToolkitFrontendPackageDirectory",
        "WebUIToolkitFrontendContractSource",
        "WebUIToolkitFrontendContractCSharpOutput",
        "WebUIToolkitFrontendContractTypeScriptOutput",
        "WebUIToolkitFrontendContractTool",
        "WebUIToolkitFrontendViteDevServerEnabled",
        "WebUIToolkitFrontendViteDevServerEntry",
        "WebUIToolkitFrontendViteConfiguration",
        "WebUIToolkitCsharpMarkupActive",
        "WebUIToolkitCsharpMarkupGeneratorAssembly",
        "WebUIToolkitCsharpMarkupManifestPath",
        "ProjectAssetsFile",
        "NETCoreSdkRuntimeIdentifier",
        "RuntimeIdentifier",
    ];

    internal bool HasContracts => !string.IsNullOrWhiteSpace(ContractSource);

    internal static async Task<DoctorProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken)
    {
        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new DevUsageException("WUTDEV1002", "The project has no parent directory.");
        CommandResult result = await CommandRunner
            .RunAsync(
                dotnetHost,
                projectDirectory,
                [
                    "msbuild",
                    project,
                    "-nologo",
                    $"-property:Configuration={configuration}",
                    $"-getProperty:{string.Join(',', PropertyNames)}",
                    "-getItem:CsharpMarkup",
                ],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException(
                "WUTDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        bool hasCsharpMarkup = document.RootElement.TryGetProperty("Items", out JsonElement items) &&
            items.TryGetProperty("CsharpMarkup", out JsonElement csharpMarkupItems) &&
            csharpMarkupItems.GetArrayLength() != 0;
        string Value(string name) =>
            properties.TryGetProperty(name, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        bool Flag(string name) =>
            bool.TryParse(Value(name), out bool enabled) && enabled;

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedProjectDirectory = Path.GetDirectoryName(evaluatedProject)
            ?? projectDirectory;
        string workspaceRoot = Normalize(
            Value("WebUIToolkitFrontendWorkspaceRoot"),
            evaluatedProjectDirectory);
        string packageDirectory = NormalizeOptional(
            Value("WebUIToolkitFrontendPackageDirectory"),
            workspaceRoot);
        string targetFramework = Value("TargetFramework");
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            targetFramework = Value("TargetFrameworks")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                is { Length: > 0 } frameworks
                    ? frameworks[0]
                    : string.Empty;
        }

        return new(
            evaluatedProject,
            evaluatedProjectDirectory,
            targetFramework,
            Flag("WebUIToolkitFrontendEnabled"),
            Flag("WebUIToolkitFrontendNodeEnabled"),
            Flag("WebUIToolkitFrontendCwhtmlEnabled"),
            workspaceRoot,
            Value("WebUIToolkitFrontendWorkspace"),
            packageDirectory,
            NormalizeOptional(
                Value("WebUIToolkitFrontendContractSource"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("WebUIToolkitFrontendContractCSharpOutput"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("WebUIToolkitFrontendContractTypeScriptOutput"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("WebUIToolkitFrontendContractTool"),
                evaluatedProjectDirectory),
            Flag("WebUIToolkitFrontendViteDevServerEnabled"),
            Value("WebUIToolkitFrontendViteDevServerEntry"),
            NormalizeOptional(
                Value("WebUIToolkitFrontendViteConfiguration"),
                packageDirectory.Length == 0 ? workspaceRoot : packageDirectory),
            NormalizeOptional(Value("ProjectAssetsFile"), evaluatedProjectDirectory),
            string.IsNullOrWhiteSpace(Value("RuntimeIdentifier"))
                ? Value("NETCoreSdkRuntimeIdentifier")
                : Value("RuntimeIdentifier"))
        {
            CsharpMarkupEnabled = hasCsharpMarkup || Flag("WebUIToolkitCsharpMarkupActive"),
            CsharpMarkupGeneratorAssembly = NormalizeOptional(
                Value("WebUIToolkitCsharpMarkupGeneratorAssembly"),
                evaluatedProjectDirectory),
            CsharpMarkupManifestPath = NormalizeOptional(
                Value("WebUIToolkitCsharpMarkupManifestPath"),
                evaluatedProjectDirectory),
        };
    }

    private static string Normalize(string path, string baseDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? baseDirectory : path, baseDirectory);

    private static string NormalizeOptional(string path, string baseDirectory) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path, baseDirectory);

    private static string Compact(string output)
    {
        string trimmed = output.Trim();
        const int maximum = 4000;
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum] + "…";
    }
}
