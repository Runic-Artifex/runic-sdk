using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal enum DoctorStatus
{
    Pass,
    Warning,
    Failure,
}

internal sealed record DoctorCheck(
    DoctorStatus Status,
    string Name,
    string Message,
    string? Remediation = null);

internal sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    internal int Passed => Checks.Count(static check => check.Status == DoctorStatus.Pass);

    internal int Warnings => Checks.Count(static check => check.Status == DoctorStatus.Warning);

    internal int Failed => Checks.Count(static check => check.Status == DoctorStatus.Failure);

    internal bool IsHealthy => Failed == 0;
}

internal interface IDoctorRuntime
{
    string? GetEnvironmentVariable(string name);

    string? FindExecutable(string name);

    Task<CommandResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class SystemDoctorRuntime : IDoctorRuntime
{
    internal static SystemDoctorRuntime Instance { get; } = new();

    private SystemDoctorRuntime()
    {
    }

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    public string? FindExecutable(string name)
    {
        if (Path.IsPathFullyQualified(name))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (string directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    public Task<CommandResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        CommandRunner.RunAsync(executable, workingDirectory, arguments, cancellationToken);
}

internal static class DoctorChecks
{
    private static readonly string[] BrowserExecutables =
        OperatingSystem.IsWindows()
            ? ["msedge", "chrome", "chromium"]
            : OperatingSystem.IsMacOS()
                ? [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium",
                ]
                : [
                    "chromium",
                    "chromium-browser",
                    "google-chrome",
                    "google-chrome-stable",
                    "microsoft-edge",
                    "microsoft-edge-stable",
                ];

    internal static async Task<DoctorReport> InspectAsync(
        DoctorProjectConfiguration project,
        string dotnetHost,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetHost);
        ArgumentNullException.ThrowIfNull(runtime);

        var checks = new List<DoctorCheck>();
        await CheckDotNetAsync(
            checks,
            project,
            dotnetHost,
            runtime,
            cancellationToken).ConfigureAwait(false);
        CheckFrontendMode(checks, project);

        string? node = await CheckNodeAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        CheckPackageManagerAndLockFile(checks, project, runtime);
        CheckNativeLibrary(checks, project, runtime);
        await CheckBrowserAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        CheckVite(checks, project);
        await CheckContractsAsync(
            checks,
            project,
            node,
            runtime,
            cancellationToken).ConfigureAwait(false);
        return new DoctorReport(checks);
    }

    private static async Task CheckDotNetAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        string dotnetHost,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        string? executable = runtime.FindExecutable(dotnetHost);
        if (executable is null)
        {
            checks.Add(Fail(
                "dotnet-sdk",
                $".NET SDK host '{dotnetHost}' is unavailable.",
                "Install the .NET 10 SDK (or the SDK targeted by the project) and put dotnet on PATH."));
            return;
        }

        CommandResult result = await runtime
            .RunAsync(
                executable,
                project.ProjectDirectory,
                ["--version"],
                cancellationToken)
            .ConfigureAwait(false);
        string versionText = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || !Version.TryParse(NormalizeVersion(versionText), out Version? sdk))
        {
            checks.Add(Fail(
                "dotnet-sdk",
                $"Could not read a usable .NET SDK version from '{executable}'.",
                "Run 'dotnet --info' and install the SDK selected by global.json or the project."));
            return;
        }

        int? targetMajor = ParseTargetFrameworkMajor(project.TargetFramework);
        if (targetMajor is not null && sdk.Major < targetMajor)
        {
            checks.Add(Fail(
                "dotnet-sdk",
                $".NET SDK {versionText} cannot build {project.TargetFramework}.",
                $"Install a .NET {targetMajor} SDK and rerun doctor."));
            return;
        }

        checks.Add(Pass(
            "dotnet-sdk",
            targetMajor is null
                ? $".NET SDK {versionText} is available."
                : $".NET SDK {versionText} can build {project.TargetFramework}."));
    }

    private static void CheckFrontendMode(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project)
    {
        if (!project.FrontendEnabled)
        {
            checks.Add(Fail(
                "frontend-sdk",
                "RunicToolkit frontend development is not enabled for the selected project.",
                "Set RunicToolkitFrontendEnabled=true only when using the optional development host."));
            return;
        }

        if (!project.NodeEnabled && !project.FrontendCompilerEnabled)
        {
            checks.Add(Fail(
                "frontend-sdk",
                "Neither the Node/Vite nor external compiler frontend pipeline is enabled.",
                "Enable RunicToolkitFrontendNodeEnabled or RunicToolkitFrontendCompilerEnabled."));
            return;
        }

        checks.Add(Pass(
            "frontend-sdk",
            project.NodeEnabled && project.FrontendCompilerEnabled
                ? "Node/Vite and external compiler frontend pipelines are enabled."
                : project.NodeEnabled
                    ? "The Node/Vite frontend pipeline is enabled."
                    : "The Node-free external compiler/static-assets pipeline is enabled."));
    }

    private static async Task<string?> CheckNodeAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        bool required = project.NodeEnabled || project.HasContracts;
        if (!required)
        {
            checks.Add(Pass("node", "Node is not required by this project."));
            return null;
        }

        string? node = runtime.FindExecutable("node");
        if (node is null)
        {
            checks.Add(Fail(
                "node",
                "Node is required but was not found on PATH.",
                "Install the Node version declared by the workspace and rerun doctor."));
            return null;
        }

        CommandResult result = await runtime
            .RunAsync(node, project.ProjectDirectory, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            checks.Add(Fail(
                "node",
                $"Node at '{node}' could not report its version.",
                "Repair the Node installation or select a working Node executable on PATH."));
            return null;
        }

        checks.Add(Pass("node", $"Node {result.StandardOutput.Trim()} is available."));
        return node;
    }

    private static void CheckPackageManagerAndLockFile(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime)
    {
        if (!project.NodeEnabled)
        {
            checks.Add(Pass(
                "package-manager",
                "No JavaScript package manager is required."));
            checks.Add(Pass("lock-file", "No JavaScript lock file is required."));
            return;
        }

        string packageJson = Path.Combine(project.WorkspaceRoot, "package.json");
        string packageManager = ReadPackageManager(packageJson)
            ?? InferPackageManager(project.WorkspaceRoot)
            ?? "npm";
        string executableName = packageManager switch
        {
            "npm" => "npm",
            "pnpm" => "pnpm",
            "yarn" => "yarn",
            _ => packageManager,
        };
        string? executable = runtime.FindExecutable(executableName);
        if (executable is null)
        {
            checks.Add(Fail(
                "package-manager",
                $"The workspace selects '{packageManager}', but '{executableName}' is unavailable.",
                $"Install/activate {packageManager} and ensure '{executableName}' is on PATH."));
        }
        else
        {
            checks.Add(Pass(
                "package-manager",
                $"{packageManager} is available at '{executable}'."));
        }

        string expectedLock = packageManager switch
        {
            "pnpm" => "pnpm-lock.yaml",
            "yarn" => "yarn.lock",
            _ => "package-lock.json",
        };
        string lockPath = Path.Combine(project.WorkspaceRoot, expectedLock);
        if (File.Exists(lockPath))
        {
            checks.Add(Pass("lock-file", $"Found reproducible workspace lock file '{lockPath}'."));
        }
        else
        {
            checks.Add(Fail(
                "lock-file",
                $"The {packageManager} workspace has no '{expectedLock}'.",
                $"Run the package manager in '{project.WorkspaceRoot}' and commit {expectedLock}."));
        }
    }

    private static void CheckNativeLibrary(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime)
    {
        string? configured = runtime.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured);
            if (File.Exists(path))
            {
                checks.Add(Pass(
                    "native-library",
                    $"CsWebUi native library is pinned by CSWEBUI_NATIVE_LIBRARY: '{path}'."));
            }
            else
            {
                checks.Add(Fail(
                    "native-library",
                    $"CSWEBUI_NATIVE_LIBRARY points to missing file '{path}'.",
                    "Correct or unset CSWEBUI_NATIVE_LIBRARY, then restore the CsWebUi.Native package."));
            }

            return;
        }

        NativeAssetResult native = FindNativeAsset(project);
        if (native.Path is not null)
        {
            checks.Add(Pass(
                "native-library",
                $"CsWebUi native asset for {project.RuntimeIdentifier} is restored at '{native.Path}'."));
        }
        else
        {
            checks.Add(Fail(
                "native-library",
                native.Message,
                $"Run 'dotnet restore \"{project.ProjectPath}\"'; for custom builds set CSWEBUI_NATIVE_LIBRARY to the native WebUI library."));
        }
    }

    private static async Task CheckBrowserAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        string? configured = runtime.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        string? browser;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            browser = Path.GetFullPath(configured);
            if (!File.Exists(browser))
            {
                checks.Add(Fail(
                    "browser",
                    $"WEBUI_BROWSER_PATH points to missing file '{browser}'.",
                    "Point WEBUI_BROWSER_PATH at Chromium, Chrome, or Edge, or unset it to use PATH discovery."));
                return;
            }
        }
        else
        {
            browser = BrowserExecutables
                .Select(runtime.FindExecutable)
                .FirstOrDefault(static path => path is not null);
            if (browser is null)
            {
                checks.Add(Fail(
                    "browser",
                    "No Chromium-family browser was found.",
                    "Install Chromium, Chrome, or Edge, or set WEBUI_BROWSER_PATH to its executable."));
                return;
            }
        }

        CommandResult result = await runtime
            .RunAsync(browser, project.ProjectDirectory, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        string version = result.StandardOutput.Trim();
        checks.Add(
            result.ExitCode == 0
                ? Pass(
                    "browser",
                    string.IsNullOrWhiteSpace(version)
                        ? $"Found browser '{browser}'."
                        : $"Found {version} at '{browser}'.")
                : Warn(
                    "browser",
                    $"Found browser '{browser}', but its version probe exited with {result.ExitCode}.",
                    "Confirm the browser can be launched by the current user."));
    }

    private static void CheckVite(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project)
    {
        if (!project.NodeEnabled)
        {
            checks.Add(Pass("vite", "Vite is not required by the Node-free frontend path."));
            return;
        }

        if (!project.ViteDevServerEnabled)
        {
            checks.Add(Pass(
                "vite",
                "Vite development-server mode is disabled; the configured frontend watcher will be used."));
            return;
        }

        bool failed = false;
        if (string.IsNullOrWhiteSpace(project.ViteConfigurationPath))
        {
            checks.Add(Warn(
                "vite-config",
                "No Vite configuration is configured; Vite defaults will be used.",
                "Add vite.config.mjs in the frontend package when custom build or HMR behavior is needed."));
        }
        else if (!File.Exists(project.ViteConfigurationPath))
        {
            failed = true;
            checks.Add(Fail(
                "vite-config",
                $"Configured Vite file '{project.ViteConfigurationPath}' does not exist.",
                "Create the file or correct RunicToolkitFrontendViteConfiguration."));
        }
        else
        {
            checks.Add(Pass(
                "vite-config",
                $"Found Vite configuration '{project.ViteConfigurationPath}'."));
        }

        string entry = project.ViteDevServerEntry;
        if (string.IsNullOrWhiteSpace(entry)
            || entry[0] != '/'
            || entry.StartsWith("//", StringComparison.Ordinal)
            || entry.Contains('\\')
            || entry.Contains('#'))
        {
            checks.Add(Fail(
                "vite-entry",
                $"Vite entry '{entry}' is not a valid root-relative module path.",
                "Set RunicToolkitFrontendViteDevServerEntry to a path such as /src/main.ts."));
            return;
        }

        string entryPath = Path.Combine(
            project.FrontendPackageDirectory,
            entry.Split('?', 2)[0].TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(entryPath))
        {
            checks.Add(Fail(
                "vite-entry",
                $"Vite entry '{entry}' resolves to missing file '{entryPath}'.",
                "Create the entry module or correct RunicToolkitFrontendViteDevServerEntry."));
            return;
        }

        if (!failed)
        {
            checks.Add(Pass("vite-entry", $"Found Vite entry module '{entryPath}'."));
        }
    }

    private static async Task CheckContractsAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        string? node,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!project.HasContracts)
        {
            checks.Add(Pass(
                "generated-contracts",
                "No generated frontend contract is configured."));
            return;
        }

        bool sourceReady = File.Exists(project.ContractSource)
            && !string.IsNullOrWhiteSpace(project.ContractTool)
            && File.Exists(project.ContractTool);
        if (!File.Exists(project.ContractSource))
        {
            checks.Add(Fail(
                "contract-source",
                $"Configured contract source '{project.ContractSource}' does not exist.",
                "Create the contract source or correct RunicToolkitFrontendContractSource."));
        }
        else if (string.IsNullOrWhiteSpace(project.ContractTool)
                 || !File.Exists(project.ContractTool))
        {
            checks.Add(Fail(
                "contract-source",
                $"Contract generator '{project.ContractTool}' does not exist.",
                "Restore the application-owned contract tool or correct RunicToolkitFrontendContractTool."));
        }
        else
        {
            checks.Add(Pass(
                "contract-source",
                $"Contract source and generator are available for '{project.ContractSource}'."));
        }

        bool outputsConfigured =
            !string.IsNullOrWhiteSpace(project.ContractCSharpOutput)
            && !string.IsNullOrWhiteSpace(project.ContractTypeScriptOutput);
        bool outputsExist =
            outputsConfigured
            && File.Exists(project.ContractCSharpOutput)
            && File.Exists(project.ContractTypeScriptOutput);
        if (!outputsConfigured)
        {
            checks.Add(Fail(
                "contract-outputs",
                "Both generated C# and TypeScript output paths must be configured.",
                "Set RunicToolkitFrontendContractCSharpOutput and RunicToolkitFrontendContractTypeScriptOutput."));
        }
        else if (!outputsExist)
        {
            checks.Add(Fail(
                "contract-outputs",
                "One or more generated contract outputs are missing.",
                "Run 'dotnet runic-toolkit dev' once to generate both contract outputs."));
        }
        else
        {
            checks.Add(Pass(
                "contract-outputs",
                "Generated C# and TypeScript contract outputs exist."));
        }

        if (!sourceReady || !outputsExist || node is null)
        {
            checks.Add(Fail(
                "contract-verify",
                "The generated contract cannot be verified with the current prerequisites.",
                "Resolve the contract source, generator, output, and Node failures above, then rerun doctor."));
            return;
        }

        CommandResult verify = await runtime
            .RunAsync(
                node,
                project.WorkspaceRoot,
                [
                    project.ContractTool,
                    "--source",
                    project.ContractSource,
                    "--csharp",
                    project.ContractCSharpOutput,
                    "--typescript",
                    project.ContractTypeScriptOutput,
                    "--verify",
                ],
                cancellationToken)
            .ConfigureAwait(false);
        if (verify.ExitCode == 0)
        {
            checks.Add(Pass(
                "contract-verify",
                "Generated contracts match their source."));
        }
        else
        {
            checks.Add(Fail(
                "contract-verify",
                $"Generated contract verification exited with {verify.ExitCode}: {Compact(verify.CombinedOutput)}",
                "Regenerate the contracts and commit the updated C# and TypeScript outputs."));
        }
    }

    private static NativeAssetResult FindNativeAsset(DoctorProjectConfiguration project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectAssetsFile)
            || !File.Exists(project.ProjectAssetsFile))
        {
            return new(
                null,
                $"NuGet assets file '{project.ProjectAssetsFile}' is missing, so the CsWebUi native library cannot be resolved.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(project.ProjectAssetsFile));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("targets", out JsonElement targets)
                || !root.TryGetProperty("libraries", out JsonElement libraries)
                || !root.TryGetProperty("packageFolders", out JsonElement packageFolders))
            {
                return new(null, "NuGet assets do not contain the expected target/package graph.");
            }

            JsonProperty? target = targets
                .EnumerateObject()
                .Where(property =>
                    string.IsNullOrWhiteSpace(project.RuntimeIdentifier)
                    || property.Name.EndsWith(
                        "/" + project.RuntimeIdentifier,
                        StringComparison.OrdinalIgnoreCase))
                .Cast<JsonProperty?>()
                .FirstOrDefault()
                ?? targets.EnumerateObject().Cast<JsonProperty?>().FirstOrDefault();
            if (target is null)
            {
                return new(null, "NuGet assets contain no target graph.");
            }

            JsonProperty? library = target.Value.Value
                .EnumerateObject()
                .Where(static property =>
                    property.Name.StartsWith("CsWebUi.Native/", StringComparison.OrdinalIgnoreCase))
                .Cast<JsonProperty?>()
                .FirstOrDefault();
            if (library is null)
            {
                return new(null, "The selected project has no restored CsWebUi.Native package.");
            }

            string? relativeNative = FindRelativeNativeAsset(
                library.Value.Value,
                project.RuntimeIdentifier);
            if (relativeNative is null)
            {
                return new(
                    null,
                    $"CsWebUi.Native has no native asset for runtime '{project.RuntimeIdentifier}'.");
            }

            if (!libraries.TryGetProperty(library.Value.Name, out JsonElement libraryMetadata)
                || !libraryMetadata.TryGetProperty("path", out JsonElement packagePathElement))
            {
                return new(null, "NuGet assets do not identify the CsWebUi.Native package path.");
            }

            string? packagePath = packagePathElement.GetString();
            string? packageRoot = packageFolders
                .EnumerateObject()
                .Select(static property => property.Name)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(packageRoot))
            {
                return new(null, "NuGet assets do not identify the package cache root.");
            }

            string nativePath = Path.GetFullPath(
                Path.Combine(
                    packageRoot,
                    packagePath.Replace('/', Path.DirectorySeparatorChar),
                    relativeNative.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(nativePath)
                ? new(nativePath, string.Empty)
                : new(null, $"Restored CsWebUi native asset '{nativePath}' is missing.");
        }
        catch (JsonException exception)
        {
            return new(
                null,
                $"NuGet assets file '{project.ProjectAssetsFile}' is invalid: {exception.Message}");
        }
    }

    private static string? FindRelativeNativeAsset(
        JsonElement library,
        string runtimeIdentifier)
    {
        if (library.TryGetProperty("runtimeTargets", out JsonElement runtimeTargets))
        {
            JsonProperty? matching = runtimeTargets
                .EnumerateObject()
                .Where(property =>
                    !property.Value.TryGetProperty("rid", out JsonElement rid)
                    || string.IsNullOrWhiteSpace(runtimeIdentifier)
                    || StringComparer.OrdinalIgnoreCase.Equals(
                        rid.GetString(),
                        runtimeIdentifier))
                .Where(static property =>
                    property.Value.TryGetProperty("assetType", out JsonElement assetType)
                    && StringComparer.Ordinal.Equals(assetType.GetString(), "native"))
                .Cast<JsonProperty?>()
                .FirstOrDefault();
            if (matching is not null)
            {
                return matching.Value.Name;
            }
        }

        if (library.TryGetProperty("native", out JsonElement native))
        {
            return native.EnumerateObject().Select(static property => property.Name).FirstOrDefault();
        }

        return null;
    }

    private static string? ReadPackageManager(string packageJson)
    {
        if (!File.Exists(packageJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(packageJson));
            if (!document.RootElement.TryGetProperty(
                    "packageManager",
                    out JsonElement packageManager))
            {
                return null;
            }

            string? declaration = packageManager.GetString();
            int separator = declaration?.IndexOf('@') ?? -1;
            return separator > 0 ? declaration![..separator] : declaration;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? InferPackageManager(string workspaceRoot) =>
        File.Exists(Path.Combine(workspaceRoot, "pnpm-lock.yaml"))
            ? "pnpm"
            : File.Exists(Path.Combine(workspaceRoot, "yarn.lock"))
                ? "yarn"
                : File.Exists(Path.Combine(workspaceRoot, "package-lock.json"))
                    ? "npm"
                    : null;

    private static int? ParseTargetFrameworkMajor(string targetFramework)
    {
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = targetFramework[3..];
        int separator = value.IndexOf('.');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        return int.TryParse(value, out int major) ? major : null;
    }

    private static string NormalizeVersion(string version)
    {
        int separator = version.IndexOfAny(['-', '+']);
        return separator > 0 ? version[..separator] : version;
    }

    private static string Compact(string output)
    {
        string compact = string.Join(
            " ",
            output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        const int maximum = 300;
        return compact.Length <= maximum ? compact : compact[..maximum] + "…";
    }

    private static DoctorCheck Pass(string name, string message) =>
        new(DoctorStatus.Pass, name, message);

    private static DoctorCheck Warn(
        string name,
        string message,
        string remediation) =>
        new(DoctorStatus.Warning, name, message, remediation);

    private static DoctorCheck Fail(
        string name,
        string message,
        string remediation) =>
        new(DoctorStatus.Failure, name, message, remediation);

    private sealed record NativeAssetResult(string? Path, string Message);
}
