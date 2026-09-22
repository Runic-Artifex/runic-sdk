using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

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
    string BridgeSource,
    string BridgeIr,
    string BridgeFacade,
    string FrontendWatchTarget,
    bool ViteDevServerEnabled,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    string FrontendCompilerDiagnosticsPath,
    string FrontendCompilerHotReloadPath,
    string TargetDirectory)
{
    internal string Host { get; init; } = "desktop";
    internal string ProjectAssetsFile { get; init; } = string.Empty;
    internal string FrontendCompilerWatchPattern { get; init; } = string.Empty;

    internal string FrontendCompilerHotReloadTarget { get; init; } = string.Empty;

    internal bool HasFrontendCompiler => FrontendCompilerEnabled;

    internal string DevelopmentServerKind { get; init; } =
        ViteDevServerEnabled ? "vite" : string.Empty;

    internal string DevelopmentServerDocument { get; init; } = "index.html";

    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "RunicHost",
        "ProjectAssetsFile",
        "RunicAssetsDist",
        "RunicAssetsEntryPoint",
        "RunicAssetsEmbeddedResourceName",
        "RunicAssetsFrontendDirectory",
        "RunicApplicationFrontendEnabled",
        "RunicApplicationFrontendNodeEnabled",
        "RunicApplicationFrontendCompilerEnabled",
        "RunicApplicationFrontendWorkspaceRoot",
        "RunicApplicationFrontendWorkspace",
        "RunicApplicationFrontendPackageDirectory",
        "RunicApplicationFrontendOutputDirectory",
        "RunicApplicationFrontendWebRoot",
        "RunicApplicationBridgeAuthority",
        "RunicApplicationBridgeSource",
        "RunicApplicationBridgeIr",
        "RunicApplicationBridgeFacade",
        "RunicApplicationFrontendDevWatchTarget",
        "RunicApplicationFrontendViteDevServerEnabled",
        "RunicApplicationFrontendViteDevServerEntry",
        "RunicApplicationFrontendViteConfiguration",
        "RunicApplicationFrontendDevServerKind",
        "RunicApplicationFrontendDevServerDocument",
        "RunicApplicationFrontendCompilerDiagnosticsPath",
        "RunicApplicationFrontendCompilerHotReloadPath",
        "RunicApplicationFrontendCompilerWatchPattern",
        "RunicApplicationFrontendCompilerHotReloadTarget",
        "TargetDir",
    ];

    internal bool HasNodeWorkspace => !string.IsNullOrWhiteSpace(Workspace);

    internal bool HasFrontendWatchTarget => !string.IsNullOrWhiteSpace(FrontendWatchTarget);

    internal bool HasFrontendWatcher => HasFrontendWatchTarget || HasNodeWorkspace;

    internal bool HasContracts => !string.IsNullOrWhiteSpace(BridgeSource);

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
            ?? throw new DevUsageException("RAPPDEV1002", "The project has no parent directory.");
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
                "RAPPDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) =>
            properties.TryGetProperty(name, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

        bool frontendEnabled = bool.TryParse(Value("RunicApplicationFrontendEnabled"), out bool enabled) && enabled;
        bool generatedAssets = !string.IsNullOrWhiteSpace(Value("RunicAssetsDist")) &&
            !string.IsNullOrWhiteSpace(Value("RunicAssetsEntryPoint"));
        if (!frontendEnabled && !generatedAssets)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The selected project must declare a generated Runic application with Runic Assets.");
        }

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedProjectDirectory = Path.GetDirectoryName(evaluatedProject)
            ?? projectDirectory;
        string canonicalFrontendDirectory = NormalizeOptional(
            Value("RunicAssetsFrontendDirectory"),
            evaluatedProjectDirectory);
        if (canonicalFrontendDirectory.Length == 0)
        {
            string conventionalFrontend = Path.Combine(evaluatedProjectDirectory, "Frontend");
            canonicalFrontendDirectory = File.Exists(Path.Combine(conventionalFrontend, "package.json"))
                ? conventionalFrontend
                : string.Empty;
        }
        bool canonicalFrontend = generatedAssets && canonicalFrontendDirectory.Length != 0;
        string workspaceRoot = canonicalFrontend
            ? canonicalFrontendDirectory
            : Normalize(Value("RunicApplicationFrontendWorkspaceRoot"), evaluatedProjectDirectory);
        string packageDirectory = NormalizeOptional(
            Value("RunicApplicationFrontendPackageDirectory"),
            workspaceRoot);
        string outputDirectory = NormalizeOptional(
            Value("RunicApplicationFrontendOutputDirectory"),
            packageDirectory.Length == 0 ? workspaceRoot : packageDirectory);
        string targetDirectory = NormalizeOptional(Value("TargetDir"), evaluatedProjectDirectory);
        if (targetDirectory.Length == 0)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "MSBuild did not evaluate TargetDir for the selected project.");
        }
        string conventionalBridgeSource = Path.Combine(canonicalFrontendDirectory, "src", "application.bridge.ts");
        string bridgeSource = NormalizeOptional(Value("RunicApplicationBridgeSource"), evaluatedProjectDirectory);
        if (bridgeSource.Length == 0 && File.Exists(conventionalBridgeSource))
        {
            bridgeSource = conventionalBridgeSource;
        }
        if (bridgeSource.Length == 0 && Value("RunicApplicationBridgeAuthority") == "csharp")
            bridgeSource = evaluatedProject;
        string bridgeIr = NormalizeOptional(Value("RunicApplicationBridgeIr"), evaluatedProjectDirectory);
        if (bridgeIr.Length == 0 && bridgeSource.Length != 0)
        {
            bridgeIr = Path.Combine(evaluatedProjectDirectory, "Contract", "bridge.ir.json");
        }
        string bridgeFacade = NormalizeOptional(Value("RunicApplicationBridgeFacade"), evaluatedProjectDirectory);
        if (bridgeFacade.Length == 0 && bridgeSource.Length != 0)
        {
            bridgeFacade = Path.Combine(canonicalFrontendDirectory, "src", "application.bridge.generated.ts");
        }

        var configurationResult = new DevProjectConfiguration(
            evaluatedProject,
            evaluatedProjectDirectory,
            canonicalFrontend || (frontendEnabled && bool.TryParse(Value("RunicApplicationFrontendNodeEnabled"), out bool nodeEnabled)
                && nodeEnabled),
            (frontendEnabled && bool.TryParse(Value("RunicApplicationFrontendCompilerEnabled"), out bool compilerEnabled)
                && compilerEnabled),
            workspaceRoot,
            canonicalFrontend ? "." : Value("RunicApplicationFrontendWorkspace"),
            canonicalFrontend ? canonicalFrontendDirectory : generatedAssets ? NormalizeOptional(Value("RunicAssetsDist"), evaluatedProjectDirectory) : packageDirectory,
            generatedAssets ? NormalizeOptional(Value("RunicAssetsDist"), evaluatedProjectDirectory) : outputDirectory,
            string.IsNullOrWhiteSpace(Value("RunicApplicationFrontendWebRoot"))
                ? "www"
                : Value("RunicApplicationFrontendWebRoot"),
            bridgeSource,
            bridgeIr,
            bridgeFacade,
            Value("RunicApplicationFrontendDevWatchTarget"),
            bool.TryParse(
                Value("RunicApplicationFrontendViteDevServerEnabled"),
                out bool viteDevServerEnabled) && viteDevServerEnabled,
            Value("RunicApplicationFrontendViteDevServerEntry"),
            NormalizeOptional(
                Value("RunicApplicationFrontendViteConfiguration"),
                packageDirectory.Length == 0 ? workspaceRoot : packageDirectory),
            NormalizeOptional(Value("RunicApplicationFrontendCompilerDiagnosticsPath"), evaluatedProjectDirectory),
            NormalizeOptional(Value("RunicApplicationFrontendCompilerHotReloadPath"), evaluatedProjectDirectory),
            targetDirectory)
        {
            ProjectAssetsFile = Normalize(Value("ProjectAssetsFile"), evaluatedProjectDirectory),
            Host = string.IsNullOrEmpty(Value("RunicHost")) ? "desktop" : Value("RunicHost"),
            FrontendCompilerWatchPattern = Value("RunicApplicationFrontendCompilerWatchPattern"),
            FrontendCompilerHotReloadTarget = Value("RunicApplicationFrontendCompilerHotReloadTarget"),
            DevelopmentServerKind =
                Value("RunicApplicationFrontendDevServerKind").Trim().ToLowerInvariant(),
            DevelopmentServerDocument =
                string.IsNullOrWhiteSpace(Value("RunicApplicationFrontendDevServerDocument"))
                    ? "index.html"
                    : Value("RunicApplicationFrontendDevServerDocument"),
        };
        configurationResult.Validate();
        return configurationResult;
    }

    private void Validate()
    {
        if (!NodeEnabled && !HasFrontendCompiler)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "Enable at least one frontend pipeline: Node/Vite or an external compiler integration.");
        }

        if (DevelopmentServerKind.Length != 0 &&
            DevelopmentServerKind is not ("vite" or "angular"))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "RunicApplicationFrontendDevServerKind must be 'vite', 'angular', or empty.");
        }

        if (HasDevelopmentServer && (!NodeEnabled || !HasNodeWorkspace))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
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
                "RAPPDEV1005",
                "RunicApplicationFrontendDevServerDocument must contain safe relative file paths " +
                "separated by semicolons.");
        }

        if (NodeEnabled && !HasNodeWorkspace && !HasFrontendWatchTarget)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "Configure RunicApplicationFrontendWorkspace or RunicApplicationFrontendDevWatchTarget.");
        }

        if (ViteDevServerEnabled
            && (!NodeEnabled
                || !HasNodeWorkspace
                || string.IsNullOrWhiteSpace(ViteDevServerEntry)))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "Vite development-server mode requires a frontend workspace and " +
                "RunicApplicationFrontendViteDevServerEntry.");
        }

        if (ViteDevServerEnabled
            && !string.IsNullOrWhiteSpace(ViteConfigurationPath)
            && !File.Exists(ViteConfigurationPath))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                $"The configured Vite file '{ViteConfigurationPath}' does not exist.");
        }

        if (ViteDevServerEnabled
            && (ViteDevServerEntry[0] != '/'
                || ViteDevServerEntry.StartsWith("//", StringComparison.Ordinal)
                || ViteDevServerEntry.Contains('\\')
                || ViteDevServerEntry.Contains('#')))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "RunicApplicationFrontendViteDevServerEntry must be a root-relative Vite module path.");
        }

        if (NodeEnabled && string.IsNullOrWhiteSpace(FrontendOutputDirectory))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "RunicApplicationFrontendOutputDirectory is required for coordinated reload.");
        }

        if (NodeEnabled
            && HasNodeWorkspace
            && (string.IsNullOrWhiteSpace(WorkspaceRoot)
                || string.IsNullOrWhiteSpace(FrontendPackageDirectory)
                || string.IsNullOrWhiteSpace(FrontendOutputDirectory)))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The frontend workspace root, package directory, and output directory are required.");
        }

        if (HasContracts
            && (string.IsNullOrWhiteSpace(BridgeIr)
                || string.IsNullOrWhiteSpace(BridgeFacade)))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "Application Bridge source, IR, and generated facade must be configured together.");
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
