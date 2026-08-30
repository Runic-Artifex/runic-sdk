using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed record DoctorProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    string TargetFramework,
    bool FrontendEnabled,
    bool NodeEnabled,
    bool FrontendCompilerEnabled,
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
    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "TargetFramework",
        "TargetFrameworks",
        "RunicAssetsDist",
        "RunicAssetsEntryPoint",
        "RunicAssetsFrontendDirectory",
        "RunicToolkitFrontendEnabled",
        "RunicToolkitFrontendNodeEnabled",
        "RunicToolkitFrontendCompilerEnabled",
        "RunicToolkitFrontendWorkspaceRoot",
        "RunicToolkitFrontendWorkspace",
        "RunicToolkitFrontendPackageDirectory",
        "RunicToolkitFrontendContractSource",
        "RunicToolkitFrontendContractCSharpOutput",
        "RunicToolkitFrontendContractTypeScriptOutput",
        "RunicToolkitFrontendContractTool",
        "RunicToolkitFrontendViteDevServerEnabled",
        "RunicToolkitFrontendViteDevServerEntry",
        "RunicToolkitFrontendViteConfiguration",
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
            ?? throw new DevUsageException("RTKDEV1002", "The project has no parent directory.");
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
                ],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException(
                "RTKDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) =>
            properties.TryGetProperty(name, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        bool Flag(string name) =>
            bool.TryParse(Value(name), out bool enabled) && enabled;

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedProjectDirectory = Path.GetDirectoryName(evaluatedProject)
            ?? projectDirectory;
        bool generatedAssets = !string.IsNullOrWhiteSpace(Value("RunicAssetsDist")) &&
            !string.IsNullOrWhiteSpace(Value("RunicAssetsEntryPoint"));
        string canonicalFrontendDirectory = NormalizeOptional(
            Value("RunicAssetsFrontendDirectory"), evaluatedProjectDirectory);
        if (canonicalFrontendDirectory.Length == 0)
        {
            string conventionalFrontend = Path.Combine(evaluatedProjectDirectory, "Frontend");
            canonicalFrontendDirectory = File.Exists(Path.Combine(conventionalFrontend, "package.json"))
                ? conventionalFrontend
                : string.Empty;
        }
        bool canonicalFrontend = generatedAssets && canonicalFrontendDirectory.Length != 0;
        string workspaceRoot = Normalize(
            canonicalFrontend ? canonicalFrontendDirectory : Value("RunicToolkitFrontendWorkspaceRoot"),
            evaluatedProjectDirectory);
        string packageDirectory = NormalizeOptional(
            Value("RunicToolkitFrontendPackageDirectory"),
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
            canonicalFrontend || Flag("RunicToolkitFrontendEnabled"),
            canonicalFrontend || Flag("RunicToolkitFrontendNodeEnabled"),
            generatedAssets || Flag("RunicToolkitFrontendCompilerEnabled"),
            workspaceRoot,
            canonicalFrontend ? "." : Value("RunicToolkitFrontendWorkspace"),
            canonicalFrontend ? canonicalFrontendDirectory : packageDirectory,
            NormalizeOptional(
                Value("RunicToolkitFrontendContractSource"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("RunicToolkitFrontendContractCSharpOutput"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("RunicToolkitFrontendContractTypeScriptOutput"),
                evaluatedProjectDirectory),
            NormalizeOptional(
                Value("RunicToolkitFrontendContractTool"),
                evaluatedProjectDirectory),
            Flag("RunicToolkitFrontendViteDevServerEnabled"),
            Value("RunicToolkitFrontendViteDevServerEntry"),
            NormalizeOptional(
                Value("RunicToolkitFrontendViteConfiguration"),
                packageDirectory.Length == 0 ? workspaceRoot : packageDirectory),
            NormalizeOptional(Value("ProjectAssetsFile"), evaluatedProjectDirectory),
            string.IsNullOrWhiteSpace(Value("RuntimeIdentifier"))
                ? Value("NETCoreSdkRuntimeIdentifier")
                : Value("RuntimeIdentifier")
        );
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
