using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
    string FrontendWatchTarget,
    bool ViteDevServerEnabled,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    string FrontendCompilerDiagnosticsPath,
    string FrontendCompilerHotReloadPath,
    string TargetDirectory)
{
    private static readonly string[] PropertyNames =
    [
        "MSBuildProjectFullPath",
        "RunicViewsWindowProject",
        "RunicBridgeFrontendDir",
        "RunicBridgeTypescriptDir",
        "RunicBridgeFrontendBuildCommand",
        "RunicAssetsDist",
        "RunicAssetsFrontendDirectory",
        "RunicApplicationFrontendEnabled",
        "RunicApplicationFrontendNodeEnabled",
        "RunicApplicationFrontendCompilerEnabled",
        "RunicApplicationFrontendWorkspaceRoot",
        "RunicApplicationFrontendWorkspace",
        "RunicApplicationFrontendPackageDirectory",
        "RunicApplicationFrontendOutputDirectory",
        "RunicApplicationFrontendWebRoot",
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
        "ProjectAssetsFile",
        "TargetDir",
    ];

    internal bool IsViewsWindowProject { get; init; }

    internal string ProjectAssetsFile { get; init; } = string.Empty;

    internal string FrontendCompilerWatchPattern { get; init; } = string.Empty;

    internal string FrontendCompilerHotReloadTarget { get; init; } = string.Empty;

    internal bool HasFrontendCompiler => FrontendCompilerEnabled;

    internal string DevelopmentServerKind { get; init; } =
        ViteDevServerEnabled ? "vite" : string.Empty;

    internal string DevelopmentServerDocument { get; init; } = "index.html";

    internal bool HasNodeWorkspace => !string.IsNullOrWhiteSpace(Workspace);

    internal bool HasFrontendWatchTarget => !string.IsNullOrWhiteSpace(FrontendWatchTarget);

    internal bool HasFrontendWatcher => HasFrontendWatchTarget || HasNodeWorkspace;

    internal bool HasDevelopmentServer => DevelopmentServerKind is "vite" or "angular";

    internal IReadOnlyList<string> DevelopmentServerDocuments =>
        DevelopmentServerDocument.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal string RuntimeWebRoot => Path.GetFullPath(Path.Combine(TargetDirectory, FrontendWebRoot));

    internal static async System.Threading.Tasks.Task<DevProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        System.Threading.CancellationToken cancellationToken)
    {
        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new DevUsageException("RAPPDEV1002", "The project has no parent directory.");
        CommandResult result = await CommandRunner.RunAsync(
            dotnetHost,
            projectDirectory,
            CreateEvaluationArguments(project, configuration),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevUsageException(
                "RAPPDEV1003",
                $"Could not evaluate '{project}'.{Environment.NewLine}{Compact(result.CombinedOutput)}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) => properties.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        bool Flag(string name) => bool.TryParse(Value(name), out bool enabled) && enabled;

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedDirectory = Path.GetDirectoryName(evaluatedProject) ?? projectDirectory;
        bool viewsWindow = Flag("RunicViewsWindowProject");
        if (!viewsWindow)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The selected project must opt into the Runic Views Window model with RunicViewsWindowProject=true.");
        }

        string frontend = NormalizeOptional(Value("RunicBridgeFrontendDir"), evaluatedDirectory);
        if (frontend.Length == 0)
        {
            frontend = NormalizeOptional(Value("RunicAssetsFrontendDirectory"), evaluatedDirectory);
        }
        if (frontend.Length == 0)
        {
            string conventional = Path.Combine(evaluatedDirectory, "Frontend");
            frontend = File.Exists(Path.Combine(conventional, "package.json")) ? conventional : string.Empty;
        }
        if (frontend.Length == 0 || !File.Exists(Path.Combine(frontend, "package.json")))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The Runic Views Window project must define RunicBridgeFrontendDir with a package.json frontend.");
        }

        string packageDirectory = NormalizeOptional(Value("RunicApplicationFrontendPackageDirectory"), frontend);
        string outputDirectory = NormalizeOptional(Value("RunicApplicationFrontendOutputDirectory"), Path.Combine(frontend, "dist"));
        string targetDirectory = NormalizeOptional(Value("TargetDir"), evaluatedDirectory);
        if (targetDirectory.Length == 0)
        {
            throw new DevUsageException("RAPPDEV1005", "MSBuild did not evaluate TargetDir for the selected project.");
        }

        string serverKind = Value("RunicApplicationFrontendDevServerKind").Trim().ToLowerInvariant();
        bool viteEnabled = Flag("RunicApplicationFrontendViteDevServerEnabled");
        if (serverKind.Length == 0)
        {
            serverKind = viteEnabled ? "vite" : File.Exists(Path.Combine(frontend, "angular.json")) ? "angular" : string.Empty;
        }

        var evaluated = new DevProjectConfiguration(
            evaluatedProject,
            evaluatedDirectory,
            NodeEnabled: true,
            FrontendCompilerEnabled: Flag("RunicApplicationFrontendCompilerEnabled"),
            WorkspaceRoot: frontend,
            Workspace: ".",
            FrontendPackageDirectory: packageDirectory,
            FrontendOutputDirectory: outputDirectory,
            FrontendWebRoot: string.IsNullOrWhiteSpace(Value("RunicApplicationFrontendWebRoot"))
                ? "www"
                : Value("RunicApplicationFrontendWebRoot"),
            FrontendWatchTarget: Value("RunicApplicationFrontendDevWatchTarget"),
            ViteDevServerEnabled: viteEnabled,
            ViteDevServerEntry: Value("RunicApplicationFrontendViteDevServerEntry"),
            ViteConfigurationPath: NormalizeOptional(Value("RunicApplicationFrontendViteConfiguration"), frontend),
            FrontendCompilerDiagnosticsPath: NormalizeOptional(Value("RunicApplicationFrontendCompilerDiagnosticsPath"), evaluatedDirectory),
            FrontendCompilerHotReloadPath: NormalizeOptional(Value("RunicApplicationFrontendCompilerHotReloadPath"), evaluatedDirectory),
            TargetDirectory: targetDirectory)
        {
            IsViewsWindowProject = viewsWindow,
            ProjectAssetsFile = NormalizeOptional(Value("ProjectAssetsFile"), evaluatedDirectory),
            FrontendCompilerWatchPattern = Value("RunicApplicationFrontendCompilerWatchPattern"),
            FrontendCompilerHotReloadTarget = Value("RunicApplicationFrontendCompilerHotReloadTarget"),
            DevelopmentServerKind = serverKind,
            DevelopmentServerDocument = string.IsNullOrWhiteSpace(Value("RunicApplicationFrontendDevServerDocument"))
                ? "index.html"
                : Value("RunicApplicationFrontendDevServerDocument"),
        };
        evaluated.Validate();
        return evaluated;
    }

    internal static IReadOnlyList<string> CreateEvaluationArguments(string project, string configuration) =>
    [
        "msbuild",
        project,
        "-nologo",
        $"-property:Configuration={configuration}",
        $"-getProperty:{string.Join(',', PropertyNames)}",
    ];

    private void Validate()
    {
        if (!NodeEnabled && !HasFrontendCompiler)
        {
            throw new DevUsageException("RAPPDEV1005", "The Views Window project requires a JavaScript frontend or an external compiler.");
        }
        if (DevelopmentServerKind.Length != 0 && DevelopmentServerKind is not ("vite" or "angular"))
        {
            throw new DevUsageException("RAPPDEV1005", "The frontend development server must be 'vite', 'angular', or empty.");
        }
        if (HasDevelopmentServer && (!HasNodeWorkspace || !NodeEnabled))
        {
            throw new DevUsageException("RAPPDEV1005", "Frontend development-server mode requires a configured package directory.");
        }
        if (HasDevelopmentServer && (DevelopmentServerDocuments.Count == 0 ||
            Array.Exists([.. DevelopmentServerDocuments], document => Path.IsPathRooted(document) ||
                Array.Exists(document.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries), segment => segment is "." or ".."))))
        {
            throw new DevUsageException("RAPPDEV1005", "The development document must contain safe relative paths separated by semicolons.");
        }
        if (ViteDevServerEnabled && (!HasNodeWorkspace || string.IsNullOrWhiteSpace(ViteDevServerEntry)))
        {
            throw new DevUsageException("RAPPDEV1005", "Vite development-server mode requires a frontend workspace and entry module.");
        }
        if (ViteDevServerEnabled && ViteDevServerEntry[0] != '/')
        {
            throw new DevUsageException("RAPPDEV1005", "The Vite entry module must be a root-relative path.");
        }
        if (ViteDevServerEnabled && ViteConfigurationPath.Length != 0 && !File.Exists(ViteConfigurationPath))
        {
            throw new DevUsageException("RAPPDEV1005", "The configured Vite file does not exist.");
        }
        if (string.IsNullOrWhiteSpace(FrontendOutputDirectory))
        {
            throw new DevUsageException("RAPPDEV1005", "The frontend output directory is required for the Views Window project.");
        }
    }

    private static string Normalize(string path, string baseDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? baseDirectory : path, baseDirectory);

    private static string NormalizeOptional(string path, string baseDirectory) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path, baseDirectory);

    private static string Compact(string output)
    {
        string trimmed = output.Trim();
        const int maximum = 4_000;
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum] + "…";
    }
}
