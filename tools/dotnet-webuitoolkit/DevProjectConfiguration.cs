using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed record DevProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    bool NodeEnabled,
    bool CwhtmlEnabled,
    string WorkspaceRoot,
    string Workspace,
    string FrontendPackageDirectory,
    string FrontendOutputDirectory,
    string FrontendWebRoot,
    string ContractSource,
    string ContractCSharpOutput,
    string ContractTypeScriptOutput,
    string ContractTool,
    string FrontendWatchTarget,
    bool ViteDevServerEnabled,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    string CwhtmlDiagnosticsPath,
    string CwhtmlHotReloadPath,
    string TargetDirectory)
{
    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "WebUIToolkitFrontendEnabled",
        "WebUIToolkitFrontendNodeEnabled",
        "WebUIToolkitFrontendCwhtmlEnabled",
        "WebUIToolkitFrontendWorkspaceRoot",
        "WebUIToolkitFrontendWorkspace",
        "WebUIToolkitFrontendPackageDirectory",
        "WebUIToolkitFrontendOutputDirectory",
        "WebUIToolkitFrontendWebRoot",
        "WebUIToolkitFrontendContractSource",
        "WebUIToolkitFrontendContractCSharpOutput",
        "WebUIToolkitFrontendContractTypeScriptOutput",
        "WebUIToolkitFrontendContractTool",
        "WebUIToolkitFrontendDevWatchTarget",
        "WebUIToolkitFrontendViteDevServerEnabled",
        "WebUIToolkitFrontendViteDevServerEntry",
        "WebUIToolkitFrontendViteConfiguration",
        "WebUIToolkitCwhtmlDiagnosticsPath",
        "WebUIToolkitCwhtmlHotReloadPath",
        "TargetDir",
    ];

    internal bool HasNodeWorkspace => !string.IsNullOrWhiteSpace(Workspace);

    internal bool HasFrontendWatchTarget => !string.IsNullOrWhiteSpace(FrontendWatchTarget);

    internal bool HasFrontendWatcher => HasFrontendWatchTarget || HasNodeWorkspace;

    internal bool HasContracts => !string.IsNullOrWhiteSpace(ContractSource);

    internal string RuntimeWebRoot => Path.GetFullPath(
        Path.Combine(TargetDirectory, FrontendWebRoot));

    internal static async Task<DevProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken)
    {
        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new DevUsageException("WUTDEV1002", "The project has no parent directory.");
        var arguments = new List<string>
        {
            "msbuild",
            project,
            "-nologo",
            $"-property:Configuration={configuration}",
            $"-getProperty:{string.Join(',', PropertyNames)}",
        };
        CommandResult result = await CommandRunner
            .RunAsync(dotnetHost, projectDirectory, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException(
                "WUTDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) =>
            properties.TryGetProperty(name, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

        if (!bool.TryParse(Value("WebUIToolkitFrontendEnabled"), out bool enabled) || !enabled)
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "The selected project does not enable WebUIToolkit.Frontend.Sdk.");
        }

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedProjectDirectory = Path.GetDirectoryName(evaluatedProject)
            ?? projectDirectory;
        string workspaceRoot = Normalize(
            Value("WebUIToolkitFrontendWorkspaceRoot"),
            evaluatedProjectDirectory);
        string packageDirectory = NormalizeOptional(
            Value("WebUIToolkitFrontendPackageDirectory"),
            workspaceRoot);
        string outputDirectory = NormalizeOptional(
            Value("WebUIToolkitFrontendOutputDirectory"),
            packageDirectory.Length == 0 ? workspaceRoot : packageDirectory);
        string targetDirectory = NormalizeOptional(Value("TargetDir"), evaluatedProjectDirectory);
        if (targetDirectory.Length == 0)
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "MSBuild did not evaluate TargetDir for the selected project.");
        }

        var configurationResult = new DevProjectConfiguration(
            evaluatedProject,
            evaluatedProjectDirectory,
            bool.TryParse(Value("WebUIToolkitFrontendNodeEnabled"), out bool nodeEnabled)
                && nodeEnabled,
            bool.TryParse(Value("WebUIToolkitFrontendCwhtmlEnabled"), out bool cwhtmlEnabled)
                && cwhtmlEnabled,
            workspaceRoot,
            Value("WebUIToolkitFrontendWorkspace"),
            packageDirectory,
            outputDirectory,
            string.IsNullOrWhiteSpace(Value("WebUIToolkitFrontendWebRoot"))
                ? "www"
                : Value("WebUIToolkitFrontendWebRoot"),
            NormalizeOptional(Value("WebUIToolkitFrontendContractSource"), evaluatedProjectDirectory),
            NormalizeOptional(Value("WebUIToolkitFrontendContractCSharpOutput"), evaluatedProjectDirectory),
            NormalizeOptional(Value("WebUIToolkitFrontendContractTypeScriptOutput"), evaluatedProjectDirectory),
            NormalizeOptional(Value("WebUIToolkitFrontendContractTool"), evaluatedProjectDirectory),
            Value("WebUIToolkitFrontendDevWatchTarget"),
            bool.TryParse(
                Value("WebUIToolkitFrontendViteDevServerEnabled"),
                out bool viteDevServerEnabled) && viteDevServerEnabled,
            Value("WebUIToolkitFrontendViteDevServerEntry"),
            NormalizeOptional(
                Value("WebUIToolkitFrontendViteConfiguration"),
                packageDirectory.Length == 0 ? workspaceRoot : packageDirectory),
            NormalizeOptional(Value("WebUIToolkitCwhtmlDiagnosticsPath"), evaluatedProjectDirectory),
            NormalizeOptional(Value("WebUIToolkitCwhtmlHotReloadPath"), evaluatedProjectDirectory),
            targetDirectory);
        configurationResult.Validate();
        return configurationResult;
    }

    private void Validate()
    {
        if (!NodeEnabled && !CwhtmlEnabled)
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "Enable at least one frontend pipeline: Node/Vite or cwhtml.");
        }

        if (NodeEnabled && !HasNodeWorkspace && !HasFrontendWatchTarget)
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "Configure WebUIToolkitFrontendWorkspace or WebUIToolkitFrontendDevWatchTarget.");
        }

        if (ViteDevServerEnabled
            && (!NodeEnabled
                || !HasNodeWorkspace
                || string.IsNullOrWhiteSpace(ViteDevServerEntry)))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "Vite development-server mode requires a frontend workspace and " +
                "WebUIToolkitFrontendViteDevServerEntry.");
        }

        if (ViteDevServerEnabled
            && !string.IsNullOrWhiteSpace(ViteConfigurationPath)
            && !File.Exists(ViteConfigurationPath))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                $"The configured Vite file '{ViteConfigurationPath}' does not exist.");
        }

        if (ViteDevServerEnabled
            && (ViteDevServerEntry[0] != '/'
                || ViteDevServerEntry.StartsWith("//", StringComparison.Ordinal)
                || ViteDevServerEntry.Contains('\\')
                || ViteDevServerEntry.Contains('#')))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "WebUIToolkitFrontendViteDevServerEntry must be a root-relative Vite module path.");
        }

        if (NodeEnabled && string.IsNullOrWhiteSpace(FrontendOutputDirectory))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "WebUIToolkitFrontendOutputDirectory is required for coordinated reload.");
        }

        if (NodeEnabled
            && HasNodeWorkspace
            && (string.IsNullOrWhiteSpace(WorkspaceRoot)
                || string.IsNullOrWhiteSpace(FrontendPackageDirectory)
                || string.IsNullOrWhiteSpace(FrontendOutputDirectory)))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "The frontend workspace root, package directory, and output directory are required.");
        }

        if (HasContracts
            && (string.IsNullOrWhiteSpace(ContractCSharpOutput)
                || string.IsNullOrWhiteSpace(ContractTypeScriptOutput)
                || string.IsNullOrWhiteSpace(ContractTool)))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "Contract source, C# output, TypeScript output, and contract tool must be configured together.");
        }
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
