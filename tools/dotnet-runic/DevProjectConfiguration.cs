using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        "RunicApplicationFrontendWindowProject",
        "RunicApplicationFrontendDirectory",
        "RunicApplicationFrontendGeneratedDirectory",
        "RunicApplicationFrontendBuildCommand",
        // The external frontend compiler integration has no documented contract yet, so its properties are internal.
        "_RunicApplicationFrontendCompilerEnabled",
        "RunicApplicationFrontendOutputDirectory",
        "RunicApplicationFrontendWebRoot",
        "RunicApplicationFrontendDevWatchTarget",
        "RunicApplicationFrontendViteDevServerEntry",
        "RunicApplicationFrontendViteConfiguration",
        "RunicApplicationFrontendDevServerKind",
        "RunicApplicationFrontendDevServerDocument",
        "_RunicApplicationFrontendCompilerDiagnosticsPath",
        "_RunicApplicationFrontendCompilerHotReloadPath",
        "_RunicApplicationFrontendCompilerWatchPattern",
        "_RunicApplicationFrontendCompilerHotReloadTarget",
        "ProjectAssetsFile",
        "TargetDir",
        // The hosts whose build files the project imports, such as "desktop;cswebui", and the one it chose.
        "_RunicApplicationHostNames",
        "RunicApplicationHost",
    ];

    internal bool IsViewsWindowProject { get; init; }

    /// <summary>
    /// Whether the resolved Vite configuration has <c>runic()</c> from <c>@runic-artifex/vite-plugin-runic</c>, as
    /// <see cref="ViteBuildInputs"/> reads it; null when dev did not resolve the configuration.
    /// </summary>
    internal bool? UsesRunicVitePlugin { get; init; }

    internal string ProjectAssetsFile { get; init; } = string.Empty;

    internal string FrontendCompilerWatchPattern { get; init; } = string.Empty;

    internal string FrontendCompilerHotReloadTarget { get; init; } = string.Empty;

    internal bool HasFrontendCompiler => FrontendCompilerEnabled;

    /// <summary>The Views hosts the restored project imports (<c>desktop</c>, <c>cswebui</c>), or none before a restore.</summary>
    internal IReadOnlyList<string> ApplicationHosts { get; init; } = [];

    /// <summary>
    /// Whether the application runs on CS-WebUI only, which serves the initial build. Runic Desktop serves the
    /// frontend output directory live; an unrestored project is assumed to be on Runic Desktop.
    /// </summary>
    internal bool RunsOnCsWebUiOnly => ApplicationHosts.Count != 0 && !ApplicationHosts.Contains("desktop");

    internal string DevelopmentServerKind { get; init; } =
        ViteDevServerEnabled ? "vite" : string.Empty;

    /// <summary>
    /// The explicit <c>RunicApplicationFrontendDevServerDocument</c> list. When it
    /// is empty, <c>dotnet runic dev</c> takes the documents from the Vite build
    /// inputs (<see cref="ViteBuildInputs"/>); the Angular server uses index.html.
    /// </summary>
    internal string DevelopmentServerDocument { get; init; } = string.Empty;

    internal bool HasExplicitDevelopmentServerDocuments => !string.IsNullOrWhiteSpace(DevelopmentServerDocument);

    internal bool HasNodeWorkspace => !string.IsNullOrWhiteSpace(Workspace);

    internal bool HasFrontendWatchTarget => !string.IsNullOrWhiteSpace(FrontendWatchTarget);

    internal bool HasFrontendWatcher => HasFrontendWatchTarget || HasNodeWorkspace;

    internal bool HasDevelopmentServer => DevelopmentServerKind is "vite" or "angular";

    internal IReadOnlyList<string> DevelopmentServerDocuments =>
        HasExplicitDevelopmentServerDocuments
            ? DevelopmentServerDocument.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : ["index.html"];

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
                "MSBuild could not evaluate the project.",
                $"Project: {project}\n{Compact(result.CombinedOutput)}\n");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Value(string name) => properties.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        bool Flag(string name) => bool.TryParse(Value(name), out bool enabled) && enabled;

        string evaluatedProject = Normalize(Value("MSBuildProjectFullPath"), projectDirectory);
        string evaluatedDirectory = Path.GetDirectoryName(evaluatedProject) ?? projectDirectory;
        bool viewsWindow = Flag("RunicApplicationFrontendWindowProject");
        if (!viewsWindow)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The selected project must opt into the Runic Views Window model with RunicApplicationFrontendWindowProject=true.");
        }

        string frontend = NormalizeOptional(Value("RunicApplicationFrontendDirectory"), evaluatedDirectory);
        if (frontend.Length == 0)
        {
            string conventional = Path.Combine(evaluatedDirectory, "Frontend");
            frontend = File.Exists(Path.Combine(conventional, "package.json")) ? conventional : string.Empty;
        }
        if (frontend.Length == 0 || !File.Exists(Path.Combine(frontend, "package.json")))
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The Runic Views Window project must define RunicApplicationFrontendDirectory with a package.json frontend.");
        }

        // The frontend directory is the package directory. The Views targets define the output directory and web root
        // for build and publish too; an unrestored project has not imported them, so the defaults repeat here.
        string packageDirectory = frontend;
        string outputDirectory = NormalizeOptional(Value("RunicApplicationFrontendOutputDirectory"), frontend);
        if (outputDirectory.Length == 0)
        {
            outputDirectory = Path.Combine(frontend, "dist");
        }
        string targetDirectory = NormalizeOptional(Value("TargetDir"), evaluatedDirectory);
        if (targetDirectory.Length == 0)
        {
            throw new DevUsageException("RAPPDEV1005", "MSBuild did not evaluate TargetDir for the selected project.");
        }

        (string serverKind, bool viteEnabled, string viteEntry, string viteConfiguration) = InferDevelopmentServer(
            frontend,
            Value("RunicApplicationFrontendDevServerKind"),
            false,
            Value("RunicApplicationFrontendViteDevServerEntry"),
            NormalizeOptional(Value("RunicApplicationFrontendViteConfiguration"), frontend));

        var evaluated = new DevProjectConfiguration(
            evaluatedProject,
            evaluatedDirectory,
            NodeEnabled: true,
            FrontendCompilerEnabled: Flag("_RunicApplicationFrontendCompilerEnabled"),
            WorkspaceRoot: frontend,
            Workspace: ".",
            FrontendPackageDirectory: packageDirectory,
            FrontendOutputDirectory: outputDirectory,
            FrontendWebRoot: string.IsNullOrWhiteSpace(Value("RunicApplicationFrontendWebRoot"))
                ? "www"
                : Value("RunicApplicationFrontendWebRoot"),
            FrontendWatchTarget: Value("RunicApplicationFrontendDevWatchTarget"),
            ViteDevServerEnabled: viteEnabled,
            ViteDevServerEntry: viteEntry,
            ViteConfigurationPath: viteConfiguration,
            FrontendCompilerDiagnosticsPath: NormalizeOptional(Value("_RunicApplicationFrontendCompilerDiagnosticsPath"), evaluatedDirectory),
            FrontendCompilerHotReloadPath: NormalizeOptional(Value("_RunicApplicationFrontendCompilerHotReloadPath"), evaluatedDirectory),
            TargetDirectory: targetDirectory)
        {
            IsViewsWindowProject = viewsWindow,
            ProjectAssetsFile = NormalizeOptional(Value("ProjectAssetsFile"), evaluatedDirectory),
            FrontendCompilerWatchPattern = Value("_RunicApplicationFrontendCompilerWatchPattern"),
            FrontendCompilerHotReloadTarget = Value("_RunicApplicationFrontendCompilerHotReloadTarget"),
            DevelopmentServerKind = serverKind,
            DevelopmentServerDocument = Value("RunicApplicationFrontendDevServerDocument"),
            ApplicationHosts = Value("RunicApplicationHost").Trim() is { Length: > 0 } chosen
                ? [chosen.ToLowerInvariant()]
                : [.. Value("_RunicApplicationHostNames").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(static name => name.ToLowerInvariant()).Distinct()],
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

    /// <summary>
    /// Infers the development server from the frontend directory when the
    /// project does not set it: angular.json selects the Angular CLI, and a
    /// Vite configuration selects Vite with the conventional src/main entry,
    /// or SvelteKit's root route.
    /// Explicit project properties always win.
    /// </summary>
    internal static (string Kind, bool ViteEnabled, string ViteEntry, string ViteConfiguration) InferDevelopmentServer(
        string frontend, string kind, bool viteEnabled, string viteEntry, string viteConfiguration)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind.Length == 0)
        {
            kind = viteEnabled ? "vite"
                : File.Exists(Path.Combine(frontend, "angular.json")) ? "angular"
                : FindFirst(frontend, ViteConfigurationFiles) is not null ? "vite"
                : string.Empty;
        }
        if (kind != "vite")
        {
            return (kind, viteEnabled, viteEntry, viteConfiguration);
        }
        if (viteConfiguration.Length == 0)
        {
            viteConfiguration = FindFirst(frontend, ViteConfigurationFiles) ?? string.Empty;
        }
        if (viteEntry.Length == 0 && FindFirst(frontend, ViteEntryFiles) is { } entry)
        {
            viteEntry = "/" + Path.GetRelativePath(frontend, entry).Replace(Path.DirectorySeparatorChar, '/');
        }
        return (kind, true, viteEntry, viteConfiguration);
    }

    private static readonly string[] ViteConfigurationFiles =
        ["vite.config.ts", "vite.config.mts", "vite.config.js", "vite.config.mjs"];

    // A SvelteKit frontend has no main module; Vite serves its root route's page or layout as the entry.
    private static readonly string[] ViteEntryFiles =
        ["src/main.ts", "src/main.tsx", "src/main.js", "src/main.jsx",
         "src/routes/+page.svelte", "src/routes/+layout.svelte", "src/routes/+layout.ts", "src/routes/+layout.js"];

    private static string? FindFirst(string directory, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            string path = Path.Combine(directory, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
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
