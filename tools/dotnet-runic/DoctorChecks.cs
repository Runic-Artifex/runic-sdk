using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

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
    private static readonly CompatibilitySetAuthority Authority = CompatibilitySetAuthority.Current;

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

        string? javaScriptRuntime = await CheckJavaScriptRuntimeAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        await CheckPackageManagerAndLockFileAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        CheckCompatibilitySet(checks, project);
        CheckOriginPolicy(checks, project);
        await CheckEmbeddedPresentationAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        await CheckBrowserAsync(
            checks,
            project,
            runtime,
            cancellationToken).ConfigureAwait(false);
        CheckVite(checks, project);
        await CheckContractsAsync(
            checks,
            project,
            javaScriptRuntime,
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

        if (!IsCompatibleToolVersion(versionText, Authority.Toolchain.DotNetSdk))
        {
            checks.Add(Fail(
                "dotnet-sdk",
                $".NET SDK {versionText} is outside the supported .NET {sdk.Major} range beginning at certified baseline {Authority.Toolchain.DotNetSdk}.",
                $"Install SDK {Authority.Toolchain.DotNetSdk} or a newer .NET {Version.Parse(Authority.Toolchain.DotNetSdk).Major} SDK."));
            return;
        }

        checks.Add(StringComparer.Ordinal.Equals(versionText, Authority.Toolchain.DotNetSdk)
            ? Pass(
                "dotnet-sdk",
                $".NET SDK {versionText} matches certified baseline {Authority.Id} and can build {project.TargetFramework}.")
            : Warn(
                "dotnet-sdk",
                $".NET SDK {versionText} is compatible with {project.TargetFramework}; certified baseline {Authority.Id} used {Authority.Toolchain.DotNetSdk}.",
                "No change is required. Reproduce certification-only issues with the exact baseline SDK."));
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
                "Neither the JavaScript/Vite nor external compiler frontend pipeline is enabled.",
                "Enable RunicToolkitFrontendNodeEnabled or RunicToolkitFrontendCompilerEnabled."));
            return;
        }

        checks.Add(Pass(
            "frontend-sdk",
            project.NodeEnabled && project.FrontendCompilerEnabled
                ? "JavaScript/Vite and external compiler frontend pipelines are enabled."
                : project.NodeEnabled
                    ? "The JavaScript/Vite frontend pipeline is enabled."
                    : "The Node-free external compiler/static-assets pipeline is enabled."));
    }

    private static async Task<string?> CheckJavaScriptRuntimeAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        bool required = project.NodeEnabled || project.HasContracts;
        if (!required)
        {
            checks.Add(Pass("javascript-runtime", "A JavaScript runtime is not required by this project."));
            return null;
        }

        JavaScriptPackageManager packageManager;
        try
        {
            packageManager = JavaScriptPackageManager.Resolve(
                project.WorkspaceRoot,
                project.FrontendPackageDirectory);
        }
        catch (DevUsageException exception)
        {
            checks.Add(Fail(
                "javascript-runtime",
                exception.Message,
                "Set packageManager to npm, pnpm, or Bun and commit its matching lock file."));
            return null;
        }

        string runtimeName = packageManager.Name == "bun" ? "bun" : "node";
        string baseline = packageManager.Name == "bun"
            ? Authority.Toolchain.Bun
            : Authority.Toolchain.Node;
        string? executable = runtime.FindExecutable(runtimeName);
        if (executable is null)
        {
            checks.Add(Fail(
                "javascript-runtime",
                $"{runtimeName} is required by the {packageManager.Name} workflow but was not found on PATH.",
                $"Install {runtimeName} {baseline} or a compatible newer release and rerun doctor."));
            return null;
        }

        CommandResult result = await runtime
            .RunAsync(executable, project.ProjectDirectory, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            checks.Add(Fail(
                "javascript-runtime",
                $"{runtimeName} at '{executable}' could not report its version.",
                $"Repair the {runtimeName} installation or select a working executable on PATH."));
            return null;
        }

        string version = result.StandardOutput.Trim().TrimStart('v');
        if (!IsCompatibleToolVersion(version, baseline))
        {
            checks.Add(Fail(
                "javascript-runtime",
                $"{runtimeName} {version} is outside the supported range beginning at certified baseline {baseline}.",
                $"Install {runtimeName} {baseline} or a newer release in the same major version."));
            return null;
        }

        checks.Add(StringComparer.Ordinal.Equals(version, baseline)
            ? Pass("javascript-runtime", $"{runtimeName} {version} matches certified baseline {Authority.Id}.")
            : Warn(
                "javascript-runtime",
                $"{runtimeName} {version} is supported; certified baseline {Authority.Id} used {baseline}.",
                $"No change is required. Reproduce certification-only issues with {runtimeName} {baseline}."));
        return executable;
    }

    private static async Task CheckPackageManagerAndLockFileAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!project.NodeEnabled)
        {
            checks.Add(Pass(
                "package-manager",
                "No JavaScript package manager is required."));
            checks.Add(Pass("lock-file", "No JavaScript lock file is required."));
            return;
        }

        JavaScriptPackageManager packageManager;
        try
        {
            packageManager = JavaScriptPackageManager.Resolve(
                project.WorkspaceRoot,
                project.FrontendPackageDirectory);
        }
        catch (DevUsageException exception)
        {
            checks.Add(Fail(
                "package-manager",
                exception.Message,
                "Set packageManager to npm, pnpm, or Bun."));
            checks.Add(Fail(
                "lock-file",
                "The expected JavaScript lock file could not be determined.",
                "Choose a supported package manager and commit its lock file."));
            return;
        }

        string? executable = runtime.FindExecutable(packageManager.Executable);
        if (executable is null)
        {
            checks.Add(Fail(
                "package-manager",
                $"The workspace selects '{packageManager.Name}', but '{packageManager.Executable}' is unavailable.",
                $"Install/activate {packageManager.Name} and ensure '{packageManager.Executable}' is on PATH."));
        }
        else
        {
            CommandResult result = await runtime
                .RunAsync(executable, project.WorkspaceRoot, ["--version"], cancellationToken)
                .ConfigureAwait(false);
            string version = result.StandardOutput.Trim().TrimStart('v');
            string baseline = packageManager.Name switch
            {
                "npm" => Authority.Toolchain.Npm,
                "pnpm" => Authority.Toolchain.Pnpm,
                "bun" => Authority.Toolchain.Bun,
                _ => throw new InvalidOperationException(),
            };
            if (result.ExitCode != 0 || !IsCompatibleToolVersion(version, baseline))
            {
                checks.Add(Fail(
                    "package-manager",
                    $"The workspace package manager '{packageManager.Name}' reported '{version}', outside the supported range beginning at {baseline}.",
                    $"Activate {packageManager.Name} {baseline} or a newer release in the same major version."));
            }
            else
            {
                string packageJson = Path.Combine(project.WorkspaceRoot, "package.json");
                string? declaredVersion = JavaScriptPackageManager.ReadDeclaredVersion(packageJson);
                checks.Add(StringComparer.Ordinal.Equals(version, baseline)
                    && (declaredVersion is null || StringComparer.Ordinal.Equals(version, declaredVersion))
                        ? Pass(
                            "package-manager",
                            $"{packageManager.Name} {version} matches certified baseline {Authority.Id}.")
                        : Warn(
                            "package-manager",
                            $"{packageManager.Name} {version} is supported; certified baseline {Authority.Id} used {baseline}.",
                            declaredVersion is not null && !StringComparer.Ordinal.Equals(version, declaredVersion)
                                ? $"Activate the packageManager-declared {packageManager.Name} {declaredVersion} for fully reproducible results."
                                : $"No change is required. Reproduce certification-only issues with {packageManager.Name} {baseline}."));
            }
        }

        string lockPath = Path.Combine(project.WorkspaceRoot, packageManager.LockFileName);
        if (File.Exists(lockPath))
        {
            checks.Add(Pass("lock-file", $"Found reproducible workspace lock file '{lockPath}'."));
        }
        else
        {
            checks.Add(Fail(
                "lock-file",
                $"The {packageManager.Name} workspace has no '{packageManager.LockFileName}'.",
                $"Run {packageManager.Name} in '{project.WorkspaceRoot}' and commit {packageManager.LockFileName}."));
        }
    }

    private static void CheckCompatibilitySet(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project)
    {
        if (!File.Exists(project.ProjectAssetsFile))
        {
            checks.Add(Fail(
                "compatibility-set",
                $"NuGet restore graph '{project.ProjectAssetsFile}' is missing.",
                $"Run 'dotnet restore \"{project.ProjectPath}\"' and rerun doctor."));
            return;
        }

        var mismatches = new List<string>();
        int selected = 0;
        try
        {
            using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(project.ProjectAssetsFile));
            if (assets.RootElement.TryGetProperty("libraries", out JsonElement libraries))
            {
                foreach (JsonProperty library in libraries.EnumerateObject())
                {
                    int separator = library.Name.LastIndexOf('/');
                    if (separator <= 0) continue;
                    string identity = library.Name[..separator];
                    string version = library.Name[(separator + 1)..];
                    string type = library.Value.TryGetProperty("type", out JsonElement typeNode)
                        ? typeNode.GetString() ?? string.Empty
                        : string.Empty;
                    if (Authority.NuGetPackages.TryGetValue(identity, out CompatibilityPackage? expected))
                    {
                        selected++;
                        if (StringComparer.Ordinal.Equals(type, "package") &&
                            !StringComparer.Ordinal.Equals(version, expected.Version))
                        {
                            mismatches.Add($"{identity} {version} (expected {expected.Version})");
                        }
                    }
                    else if (StringComparer.OrdinalIgnoreCase.Equals(type, "package") &&
                             IsRunicIdentity(identity))
                    {
                        mismatches.Add($"{identity} {version} (not selected by {Authority.Id})");
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            checks.Add(Fail(
                "compatibility-set",
                $"NuGet restore graph could not be read: {Compact(exception.Message)}",
                "Delete obj/project.assets.json, restore the project, and rerun doctor."));
            return;
        }

        CheckJavaScriptManifestCompatibility(project, mismatches, ref selected);
        CheckNpmLockCompatibility(project, mismatches);
        if (mismatches.Count != 0)
        {
            checks.Add(Fail(
                "compatibility-set",
                $"Compatibility set {Authority.Id} does not match: {string.Join("; ", mismatches)}.",
                $"Select the exact packages recorded by {Authority.Id}, restore with an isolated feed, and rerun doctor."));
        }
        else if (selected == 0)
        {
            checks.Add(Warn(
                "compatibility-set",
                $"No package selected by compatibility set {Authority.Id} was found in the restore graphs.",
                "Restore the generated Runic application before running doctor."));
        }
        else
        {
            checks.Add(Pass(
                "compatibility-set",
                $"{selected} selected package(s) match {Authority.Id} ({Authority.ReleaseTrainVersion})."));
        }
    }

    private static void CheckJavaScriptManifestCompatibility(
        DoctorProjectConfiguration project,
        List<string> mismatches,
        ref int selected)
    {
        string packageJson = Path.Combine(project.WorkspaceRoot, "package.json");
        if (!project.NodeEnabled || !File.Exists(packageJson)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(packageJson));
            foreach (string sectionName in new[] { "dependencies", "devDependencies", "optionalDependencies" })
            {
                if (!document.RootElement.TryGetProperty(sectionName, out JsonElement section) ||
                    section.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty package in section.EnumerateObject())
                {
                    string version = package.Value.GetString() ?? string.Empty;
                    if (Authority.NpmPackages.TryGetValue(package.Name, out CompatibilityPackage? expected))
                    {
                        selected++;
                        if (!StringComparer.Ordinal.Equals(version, expected.Version))
                        {
                            mismatches.Add($"{package.Name} {version} (expected {expected.Version})");
                        }
                    }
                    else if (package.Name.StartsWith("@runic-artifex/", StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add($"{package.Name} {version} (not selected by {Authority.Id})");
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            mismatches.Add($"package.json is unreadable ({Compact(exception.Message)})");
        }
    }

    private static void CheckNpmLockCompatibility(
        DoctorProjectConfiguration project,
        List<string> mismatches)
    {
        string lockPath = Path.Combine(project.WorkspaceRoot, "package-lock.json");
        if (!project.NodeEnabled || !File.Exists(lockPath)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
            if (!document.RootElement.TryGetProperty("lockfileVersion", out JsonElement lockfileVersion) ||
                !lockfileVersion.TryGetInt32(out int lockVersion) ||
                lockVersion != 3)
            {
                mismatches.Add("package-lock.json is not lockfileVersion 3");
                return;
            }
            if (!document.RootElement.TryGetProperty("packages", out JsonElement packages))
            {
                mismatches.Add("package-lock.json has no package entries");
                return;
            }
            foreach (JsonProperty package in packages.EnumerateObject())
            {
                const string prefix = "node_modules/";
                if (!package.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string identity = package.Name[prefix.Length..];
                string version = package.Value.TryGetProperty("version", out JsonElement versionNode)
                    ? versionNode.GetString() ?? string.Empty
                    : string.Empty;
                if (Authority.NpmPackages.TryGetValue(identity, out CompatibilityPackage? expected))
                {
                    if (!StringComparer.Ordinal.Equals(version, expected.Version))
                        mismatches.Add($"{identity} {version} (expected {expected.Version})");
                    if (!package.Value.TryGetProperty("integrity", out JsonElement integrity) ||
                        !(integrity.GetString() ?? string.Empty).StartsWith("sha512-", StringComparison.Ordinal))
                    {
                        mismatches.Add($"{identity} has no sha512 lock integrity");
                    }
                    if (package.Value.TryGetProperty("resolved", out _))
                    {
                        mismatches.Add($"{identity} lock pins a registry host");
                    }
                }
                else if (identity.StartsWith("@runic-artifex/", StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{identity} {version} (not selected by {Authority.Id})");
                }
            }
        }
        catch (JsonException exception)
        {
            mismatches.Add($"package-lock.json is unreadable ({Compact(exception.Message)})");
        }
    }

    private static void CheckOriginPolicy(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project)
    {
        if (HasResolvedLibrary(project.ProjectAssetsFile, "Runic.Desktop"))
        {
            checks.Add(Pass(
                "origin-policy",
                "Runic Desktop owns a private loopback origin; public binding is not selected by the Desktop profile."));
            return;
        }

        if (!HasResolvedLibrary(project.ProjectAssetsFile, "Runic.Application.Hosting"))
        {
            checks.Add(Pass("origin-policy", "No hosted public-origin profile is selected."));
            return;
        }

        string settingsPath = Path.Combine(project.ProjectDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            checks.Add(Warn(
                "origin-policy",
                "The hosted profile is selected, but appsettings.json does not declare its public origin.",
                "Provide Runic:HostedDeployment:PublicOrigin as one HTTPS origin and explicit TrustedProxyAddresses through configuration."));
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            JsonElement hosted = default;
            bool hasSection = document.RootElement.TryGetProperty("Runic", out JsonElement runic) &&
                runic.TryGetProperty("HostedDeployment", out hosted);
            string? origin = hasSection && hosted.TryGetProperty("PublicOrigin", out JsonElement originNode)
                ? originNode.GetString()
                : null;
            string? proxies = hasSection && hosted.TryGetProperty("TrustedProxyAddresses", out JsonElement proxyNode)
                ? proxyNode.GetString()
                : null;
            bool validOrigin = Uri.TryCreate(origin, UriKind.Absolute, out Uri? value) &&
                StringComparer.OrdinalIgnoreCase.Equals(value.Scheme, Uri.UriSchemeHttps) &&
                string.IsNullOrEmpty(value.UserInfo) &&
                StringComparer.Ordinal.Equals(value.AbsoluteUri, value.GetLeftPart(UriPartial.Authority) + "/");
            if (!validOrigin || string.IsNullOrWhiteSpace(proxies))
            {
                checks.Add(Fail(
                    "origin-policy",
                    "Hosted origin configuration is incomplete or is not one exact HTTPS origin with explicit trusted proxies.",
                    "Set Runic:HostedDeployment:PublicOrigin to one HTTPS scheme-and-authority value and TrustedProxyAddresses to explicit non-wildcard addresses."));
            }
            else
            {
                checks.Add(Pass("origin-policy", $"Hosted public origin '{origin}' is explicit and proxy-bounded."));
            }
        }
        catch (JsonException exception)
        {
            checks.Add(Fail(
                "origin-policy",
                $"appsettings.json is unreadable: {Compact(exception.Message)}",
                "Repair appsettings.json and rerun doctor."));
        }
    }

    private static async Task CheckEmbeddedPresentationAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!HasResolvedLibrary(project.ProjectAssetsFile, "Runic.Desktop"))
        {
            checks.Add(Pass("embedded-webview", "Runic Desktop embedded presentation is not selected."));
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            checks.Add(Pass("embedded-webview", "WKWebView is supplied by macOS; application startup must remain on the main thread."));
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            string? fixedRuntime = runtime.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
            if (!string.IsNullOrWhiteSpace(fixedRuntime) && Directory.Exists(fixedRuntime))
            {
                checks.Add(Pass("embedded-webview", $"Found the configured WebView2 runtime at '{fixedRuntime}'."));
            }
            else
            {
                checks.Add(Warn(
                    "embedded-webview",
                    "A fixed WebView2 runtime was not configured; Evergreen runtime discovery will occur at startup.",
                    "Install the Microsoft Edge WebView2 Runtime or select Browser presentation explicitly."));
            }
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            string? pkgConfig = runtime.FindExecutable("pkg-config");
            if (pkgConfig is null)
            {
                checks.Add(Warn(
                    "embedded-webview",
                    "WebKitGTK could not be probed because pkg-config is unavailable.",
                    "Install pkg-config plus webkit2gtk-4.1, or select Browser presentation explicitly."));
                return;
            }
            CommandResult result = await runtime
                .RunAsync(pkgConfig, project.ProjectDirectory, ["--exists", "webkit2gtk-4.1"], cancellationToken)
                .ConfigureAwait(false);
            checks.Add(result.ExitCode == 0
                ? Pass("embedded-webview", "WebKitGTK 4.1 is available for embedded presentation.")
                : Warn(
                    "embedded-webview",
                    "WebKitGTK 4.1 was not found.",
                    "Install webkit2gtk-4.1, or select Browser presentation explicitly."));
            return;
        }

        checks.Add(Warn(
            "embedded-webview",
            $"Embedded presentation is not certified for runtime identifier '{project.RuntimeIdentifier}'.",
            "Select Browser presentation or use a certified platform profile."));
    }

    private static bool HasResolvedLibrary(string assetsPath, string identity)
    {
        if (!File.Exists(assetsPath)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries)) return false;
            return libraries.EnumerateObject().Any(item =>
                item.Name.StartsWith(identity + "/", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsRunicIdentity(string identity) =>
        identity.StartsWith("Runic", StringComparison.OrdinalIgnoreCase) ||
        identity.StartsWith("dotnet-runic", StringComparison.OrdinalIgnoreCase) ||
        identity.StartsWith("CsWebUi", StringComparison.OrdinalIgnoreCase);

    private static async Task CheckBrowserAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        string? configured = runtime.GetEnvironmentVariable("RUNIC_BROWSER_PATH");
        string? browser;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            browser = Path.GetFullPath(configured);
            if (!File.Exists(browser))
            {
                checks.Add(Fail(
                    "browser",
                    $"RUNIC_BROWSER_PATH points to missing file '{browser}'.",
                    "Point RUNIC_BROWSER_PATH at Chromium, Chrome, or Edge, or unset it to use PATH discovery."));
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
                    "Install Chromium, Chrome, or Edge, or set RUNIC_BROWSER_PATH to its executable."));
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

        bool sourceReady = File.Exists(project.BridgeSource);
        if (!sourceReady)
        {
            checks.Add(Fail(
                "contract-source",
                $"Configured bridge source '{project.BridgeSource}' does not exist.",
                "Create src/application.bridge.ts or correct RunicApplicationBridgeSource."));
        }
        else
        {
            checks.Add(Pass(
                "contract-source",
                $"Found handwritten bridge source '{project.BridgeSource}'."));
        }

        bool outputsConfigured =
            !string.IsNullOrWhiteSpace(project.BridgeIr)
            && !string.IsNullOrWhiteSpace(project.BridgeFacade);
        bool outputsExist =
            outputsConfigured
            && File.Exists(project.BridgeIr)
            && File.Exists(project.BridgeFacade);
        if (!outputsConfigured)
        {
            checks.Add(Fail(
                "contract-outputs",
                "Both Bridge IR and fingerprint facade output paths must be configured.",
                "Set RunicApplicationBridgeIr and RunicApplicationBridgeFacade."));
        }
        else if (!outputsExist)
        {
            checks.Add(Fail(
                "contract-outputs",
                "Bridge IR or its fingerprint facade is missing.",
                "Run the frontend contract:generate script."));
        }
        else
        {
            checks.Add(Pass(
                "contract-outputs",
                "Bridge IR and its fingerprint facade exist."));
        }

        if (!sourceReady || !outputsExist || node is null)
        {
            checks.Add(Fail(
                "contract-verify",
                "The generated contract cannot be verified with the current prerequisites.",
                "Resolve the bridge source, output, and Node failures above, then rerun doctor."));
            return;
        }

        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            project.WorkspaceRoot,
            project.FrontendPackageDirectory);
        string? packageManagerExecutable = runtime.FindExecutable(packageManager.Executable);
        if (packageManagerExecutable is null)
        {
            checks.Add(Fail(
                "contract-verify",
                $"The {packageManager.Name} executable is unavailable for contract verification.",
                $"Install {packageManager.Name} and rerun doctor."));
            return;
        }
        CommandResult verify = await runtime
            .RunAsync(
                packageManagerExecutable,
                project.FrontendPackageDirectory,
                packageManager.RunScriptArguments("contract:check", "."),
                cancellationToken)
            .ConfigureAwait(false);
        if (verify.ExitCode == 0)
        {
            checks.Add(Pass(
                "contract-verify",
                "Generated Bridge IR and facade match their source."));
        }
        else
        {
            checks.Add(Fail(
                "contract-verify",
                $"Generated contract verification exited with {verify.ExitCode}: {Compact(verify.CombinedOutput)}",
                "Regenerate and commit the Bridge IR and fingerprint facade."));
        }
    }

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

    private static bool IsCompatibleToolVersion(string actual, string baseline)
    {
        if (!Version.TryParse(NormalizeVersion(actual), out Version? actualVersion) ||
            !Version.TryParse(NormalizeVersion(baseline), out Version? baselineVersion))
        {
            return false;
        }

        return actualVersion.Major == baselineVersion.Major && actualVersion >= baselineVersion;
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

}
