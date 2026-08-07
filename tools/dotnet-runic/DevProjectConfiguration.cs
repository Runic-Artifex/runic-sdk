using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal sealed record DevProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    bool NodeEnabled,
    bool FrontendCompilerEnabled,
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
    string FrontendCompilerDiagnosticsPath,
    string FrontendCompilerHotReloadPath,
    string TargetDirectory)
{
    internal string FrontendCompilerWatchPattern { get; init; } = string.Empty;

    internal string FrontendCompilerHotReloadTarget { get; init; } = string.Empty;

    internal bool HasFrontendCompiler => FrontendCompilerEnabled;

    internal string DevelopmentServerKind { get; init; } =
        ViteDevServerEnabled ? "vite" : string.Empty;

    internal string DevelopmentServerDocument { get; init; } = "index.html";

    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "RunicToolkitFrontendEnabled",
        "RunicToolkitFrontendNodeEnabled",
        "RunicToolkitFrontendCompilerEnabled",
        "RunicToolkitFrontendWorkspaceRoot",
        "RunicToolkitFrontendWorkspace",
        "RunicToolkitFrontendPackageDirectory",
        "RunicToolkitFrontendOutputDirectory",
        "RunicToolkitFrontendWebRoot",
        "RunicToolkitFrontendContractSource",
        "RunicToolkitFrontendContractCSharpOutput",
        "RunicToolkitFrontendContractTypeScriptOutput",
        "RunicToolkitFrontendContractTool",
        "RunicToolkitFrontendDevWatchTarget",
        "RunicToolkitFrontendViteDevServerEnabled",
        "RunicToolkitFrontendViteDevServerEntry",
        "RunicToolkitFrontendViteConfiguration",
        "RunicToolkitFrontendDevServerKind",
        "RunicToolkitFrontendDevServerDocument",
        "RunicToolkitFrontendCompilerDiagnosticsPath",
        "RunicToolkitFrontendCompilerHotReloadPath",
        "RunicToolkitFrontendCompilerWatchPattern",
        "RunicToolkitFrontendCompilerHotReloadTarget",
        "TargetDir",
    ];

    internal bool HasNodeWorkspace => !string.IsNullOrWhiteSpace(Workspace);

    internal bool HasFrontendWatchTarget => !string.IsNullOrWhiteSpace(FrontendWatchTarget);

    internal bool HasFrontendWatcher => HasFrontendWatchTarget || HasNodeWorkspace;

    internal bool HasContracts => !string.IsNullOrWhiteSpace(ContractSource);

    internal bool HasDevelopmentServer =>
        DevelopmentServerKind is "vite" or "angular";

    internal IReadOnlyList<string> DevelopmentServerDocuments =>
        DevelopmentServerDocument
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal string RuntimeWebRoot => Path.GetFullPath(
        Path.Combine(TargetDirectory, FrontendWebRoot));

    internal static async Task<DevProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken)
    {
        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new DevUsageException("RTKDEV1002", "The project has no parent directory.");
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
                "RTKDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) =>
            properties.TryGetProperty(name, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

        if (!bool.TryParse(Value("RunicToolkitFrontendEnabled"), out bool enabled) || !enabled)
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "The selected project does not enable the RunicToolkit frontend development properties.");
        }

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedProjectDirectory = Path.GetDirectoryName(evaluatedProject)
            ?? projectDirectory;
        string workspaceRoot = Normalize(
            Value("RunicToolkitFrontendWorkspaceRoot"),
            evaluatedProjectDirectory);
        string packageDirectory = NormalizeOptional(
            Value("RunicToolkitFrontendPackageDirectory"),
            workspaceRoot);
        string outputDirectory = NormalizeOptional(
            Value("RunicToolkitFrontendOutputDirectory"),
            packageDirectory.Length == 0 ? workspaceRoot : packageDirectory);
        string targetDirectory = NormalizeOptional(Value("TargetDir"), evaluatedProjectDirectory);
        if (targetDirectory.Length == 0)
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "MSBuild did not evaluate TargetDir for the selected project.");
        }

        var configurationResult = new DevProjectConfiguration(
            evaluatedProject,
            evaluatedProjectDirectory,
            bool.TryParse(Value("RunicToolkitFrontendNodeEnabled"), out bool nodeEnabled)
                && nodeEnabled,
            bool.TryParse(Value("RunicToolkitFrontendCompilerEnabled"), out bool compilerEnabled)
                && compilerEnabled,
            workspaceRoot,
            Value("RunicToolkitFrontendWorkspace"),
            packageDirectory,
            outputDirectory,
            string.IsNullOrWhiteSpace(Value("RunicToolkitFrontendWebRoot"))
                ? "www"
                : Value("RunicToolkitFrontendWebRoot"),
            NormalizeOptional(Value("RunicToolkitFrontendContractSource"), evaluatedProjectDirectory),
            NormalizeOptional(Value("RunicToolkitFrontendContractCSharpOutput"), evaluatedProjectDirectory),
            NormalizeOptional(Value("RunicToolkitFrontendContractTypeScriptOutput"), evaluatedProjectDirectory),
            NormalizeOptional(Value("RunicToolkitFrontendContractTool"), evaluatedProjectDirectory),
            Value("RunicToolkitFrontendDevWatchTarget"),
            bool.TryParse(
                Value("RunicToolkitFrontendViteDevServerEnabled"),
                out bool viteDevServerEnabled) && viteDevServerEnabled,
            Value("RunicToolkitFrontendViteDevServerEntry"),
            NormalizeOptional(
                Value("RunicToolkitFrontendViteConfiguration"),
                packageDirectory.Length == 0 ? workspaceRoot : packageDirectory),
            NormalizeOptional(Value("RunicToolkitFrontendCompilerDiagnosticsPath"), evaluatedProjectDirectory),
            NormalizeOptional(Value("RunicToolkitFrontendCompilerHotReloadPath"), evaluatedProjectDirectory),
            targetDirectory)
        {
            FrontendCompilerWatchPattern = Value("RunicToolkitFrontendCompilerWatchPattern"),
            FrontendCompilerHotReloadTarget = Value("RunicToolkitFrontendCompilerHotReloadTarget"),
            DevelopmentServerKind =
                Value("RunicToolkitFrontendDevServerKind").Trim().ToLowerInvariant(),
            DevelopmentServerDocument =
                string.IsNullOrWhiteSpace(Value("RunicToolkitFrontendDevServerDocument"))
                    ? "index.html"
                    : Value("RunicToolkitFrontendDevServerDocument"),
        };
        configurationResult.Validate();
        return configurationResult;
    }

    private void Validate()
    {
        if (!NodeEnabled && !HasFrontendCompiler)
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "Enable at least one frontend pipeline: Node/Vite or an external compiler integration.");
        }

        if (DevelopmentServerKind.Length != 0 &&
            DevelopmentServerKind is not ("vite" or "angular"))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "RunicToolkitFrontendDevServerKind must be 'vite', 'angular', or empty.");
        }

        if (HasDevelopmentServer && (!NodeEnabled || !HasNodeWorkspace))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "Frontend development-server mode requires a configured Node workspace.");
        }

        if (HasDevelopmentServer &&
            (DevelopmentServerDocuments.Count == 0 ||
             Array.Exists(
                 [.. DevelopmentServerDocuments],
                 static document =>
                     Path.IsPathRooted(document) ||
                     Array.Exists(
                         document.Split(
                             ['/', '\\'],
                             StringSplitOptions.RemoveEmptyEntries),
                         static segment => segment is "." or ".."))))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "RunicToolkitFrontendDevServerDocument must contain safe relative file paths " +
                "separated by semicolons.");
        }

        if (NodeEnabled && !HasNodeWorkspace && !HasFrontendWatchTarget)
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "Configure RunicToolkitFrontendWorkspace or RunicToolkitFrontendDevWatchTarget.");
        }

        if (ViteDevServerEnabled
            && (!NodeEnabled
                || !HasNodeWorkspace
                || string.IsNullOrWhiteSpace(ViteDevServerEntry)))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "Vite development-server mode requires a frontend workspace and " +
                "RunicToolkitFrontendViteDevServerEntry.");
        }

        if (ViteDevServerEnabled
            && !string.IsNullOrWhiteSpace(ViteConfigurationPath)
            && !File.Exists(ViteConfigurationPath))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                $"The configured Vite file '{ViteConfigurationPath}' does not exist.");
        }

        if (ViteDevServerEnabled
            && (ViteDevServerEntry[0] != '/'
                || ViteDevServerEntry.StartsWith("//", StringComparison.Ordinal)
                || ViteDevServerEntry.Contains('\\')
                || ViteDevServerEntry.Contains('#')))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "RunicToolkitFrontendViteDevServerEntry must be a root-relative Vite module path.");
        }

        if (NodeEnabled && string.IsNullOrWhiteSpace(FrontendOutputDirectory))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "RunicToolkitFrontendOutputDirectory is required for coordinated reload.");
        }

        if (NodeEnabled
            && HasNodeWorkspace
            && (string.IsNullOrWhiteSpace(WorkspaceRoot)
                || string.IsNullOrWhiteSpace(FrontendPackageDirectory)
                || string.IsNullOrWhiteSpace(FrontendOutputDirectory)))
        {
            throw new DevUsageException(
                "RTKDEV1005",
                "The frontend workspace root, package directory, and output directory are required.");
        }

        if (HasContracts
            && (string.IsNullOrWhiteSpace(ContractCSharpOutput)
                || string.IsNullOrWhiteSpace(ContractTypeScriptOutput)
                || string.IsNullOrWhiteSpace(ContractTool)))
        {
            throw new DevUsageException(
                "RTKDEV1005",
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
