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
    DoctorHostPlatform Platform { get; }
    /// <summary>Finds a native library by file name in the loader search path without loading it.</summary>
    Task<bool> IsNativeLibraryAvailableAsync(string fileName, CancellationToken cancellationToken);
    /// <summary>The installed Microsoft Edge WebView2 Runtime version, or null.</summary>
    string? GetWebView2RuntimeVersion();
    Task<CommandResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class SystemDoctorRuntime : IDoctorRuntime
{
    internal static SystemDoctorRuntime Instance { get; } = new();
    private SystemDoctorRuntime() { }

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? FindExecutable(string name)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? Path.GetFullPath(name) : null;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        // A relative PATH entry would resolve against the project directory, so a
        // checked-out file could run as pkg-config, ldconfig or a compiler.
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified))
        foreach (string extension in extensions)
        {
            string candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public Task<CommandResult> RunAsync(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        CommandRunner.RunAsync(executable, workingDirectory, arguments, cancellationToken);

    public DoctorHostPlatform Platform { get; } = new(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown",
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        IsMuslHost(),
        Environment.OSVersion.Version.ToString());

    // The tool's RID is portable linux-x64 on a musl host too, so ask which
    // dynamic loader this process uses. A glibc distribution can install musl
    // beside glibc, so /lib/ld-musl-* only decides when the process maps are
    // unreadable or name neither loader.
    private static bool IsMuslHost()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal)) return true;
        try
        {
            string maps = File.ReadAllText("/proc/self/maps");
            if (maps.Contains("/ld-musl-", StringComparison.Ordinal)) return true;
            if (maps.Contains("/ld-linux", StringComparison.Ordinal)) return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Mirrors Runic.Desktop's LinuxDesktopRuntime.IsLibraryAvailable: the loader
    // path, the standard library directories, then the ldconfig cache.
    public async Task<bool> IsNativeLibraryAvailableAsync(string fileName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return false;
        string triplet = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        IEnumerable<string> directories = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Concat(["/lib", "/usr/lib", "/lib64", "/usr/lib64", $"/lib/{triplet}-linux-gnu", $"/usr/lib/{triplet}-linux-gnu"]);
        if (directories.Any(directory => File.Exists(Path.Combine(directory, fileName)))) return true;
        string? ldconfig = File.Exists("/sbin/ldconfig") ? "/sbin/ldconfig" : FindExecutable("ldconfig");
        if (ldconfig is null) return false;
        try
        {
            CommandResult cache = await CommandRunner.RunAsync(ldconfig, Path.GetTempPath(), ["-p"], cancellationToken)
                .ConfigureAwait(false);
            return cache.ExitCode == 0 && cache.StandardOutput.Split('\n').Any(line =>
                line.TrimStart().StartsWith(fileName + " ", StringComparison.Ordinal) &&
                line.Split("=>", StringSplitOptions.TrimEntries) is [_, var path] && File.Exists(path));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or DevDevelopmentException)
        {
            return false;
        }
    }

    public string? GetWebView2RuntimeVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // The evergreen runtime registers its version under EdgeUpdate, per machine or per user.
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach ((Microsoft.Win32.RegistryKey root, string key) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\" + client),
            (Microsoft.Win32.Registry.CurrentUser, @"Software\" + client),
        })
        {
            using Microsoft.Win32.RegistryKey? entry = root.OpenSubKey(key);
            if (entry?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return version;
        }
        return null;
    }
}

internal static class DoctorChecks
{
    private static readonly CompatibilitySetAuthority Authority = CompatibilitySetAuthority.Current;

    internal static async Task<DoctorReport> InspectAsync(
        DoctorProjectConfiguration project,
        string dotnetHost,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken,
        DoctorTargetRid? target = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetHost);
        ArgumentNullException.ThrowIfNull(runtime);
        var checks = new List<DoctorCheck>();
        await CheckDotNetAsync(checks, project, dotnetHost, runtime, cancellationToken).ConfigureAwait(false);
        CheckViewsWindow(checks, project);
        _ = await CheckJavaScriptRuntimeAsync(checks, project, runtime, cancellationToken).ConfigureAwait(false);
        await CheckPackageManagerAsync(checks, project, runtime, cancellationToken).ConfigureAwait(false);
        CheckCompatibilitySet(checks, project);
        CheckFrontendDevelopment(checks, project);
        CheckBrowser(checks, project, runtime);
        if (target is not null)
        {
            await DoctorTargetChecks.InspectAsync(checks, project, target, runtime, cancellationToken).ConfigureAwait(false);
        }
        return new DoctorReport(checks);
    }

    private static async Task CheckDotNetAsync(List<DoctorCheck> checks, DoctorProjectConfiguration project,
        string dotnetHost, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        string? executable = runtime.FindExecutable(dotnetHost);
        if (executable is null)
        {
            checks.Add(Fail("dotnet-sdk", $".NET SDK host '{Path.GetFileName(dotnetHost)}' is unavailable.", "Install the SDK targeted by this project and put dotnet on PATH."));
            return;
        }
        CommandResult result = await runtime.RunAsync(executable, project.ProjectDirectory, ["--version"], cancellationToken).ConfigureAwait(false);
        string version = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || !Version.TryParse(NormalizeVersion(version), out Version? sdk))
        {
            checks.Add(Fail("dotnet-sdk", $"Could not read a usable .NET SDK version from '{Path.GetFileName(executable)}'.", "Run dotnet --info and install the SDK selected by global.json."));
            return;
        }
        int? targetMajor = ParseTargetFrameworkMajor(project.TargetFramework);
        if (targetMajor is not null && sdk.Major < targetMajor)
        {
            checks.Add(Fail("dotnet-sdk", $".NET SDK {version} cannot build {project.TargetFramework}.", $"Install a .NET {targetMajor} SDK."));
            return;
        }
        if (!IsCompatibleToolVersion(version, Authority.Toolchain.DotNetSdk))
        {
            checks.Add(Fail("dotnet-sdk", $".NET SDK {version} is outside the supported range beginning at {Authority.Toolchain.DotNetSdk}.", $"Install SDK {Authority.Toolchain.DotNetSdk} or a newer release in the same major version."));
            return;
        }
        checks.Add(StringComparer.Ordinal.Equals(version, Authority.Toolchain.DotNetSdk)
            ? Pass("dotnet-sdk", $".NET SDK {version} matches certified baseline {Authority.Id} and can build {project.TargetFramework}.")
            : Warn("dotnet-sdk", $".NET SDK {version} is compatible with {project.TargetFramework}; certification used {Authority.Toolchain.DotNetSdk}.", "No change is required."));
    }

    private static void CheckViewsWindow(List<DoctorCheck> checks, DoctorProjectConfiguration project)
    {
        if (!project.IsViewsWindowProject)
        {
            checks.Add(Fail("views-window", "The project does not opt into Runic Views Window.", "Set RunicViewsWindowProject=true and reference Runic.Application.CsWebUi or Runic.Application.Desktop."));
            return;
        }
        checks.Add(Pass("views-window", "The project uses the Runic Views Window model."));
    }

    private static async Task<string?> CheckJavaScriptRuntimeAsync(List<DoctorCheck> checks,
        DoctorProjectConfiguration project, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        JavaScriptPackageManager packageManager;
        try { packageManager = JavaScriptPackageManager.Resolve(project.FrontendPackageDirectory, project.FrontendPackageDirectory); }
        catch (DevUsageException error)
        {
            checks.Add(Fail("javascript-runtime", error.Message, "Set packageManager to npm, pnpm, or Bun."));
            return null;
        }
        string executableName = packageManager.Name == "bun" ? "bun" : "node";
        string baseline = packageManager.Name == "bun" ? Authority.Toolchain.Bun : Authority.Toolchain.Node;
        string? executable = runtime.FindExecutable(executableName);
        if (executable is null)
        {
            checks.Add(Fail("javascript-runtime", $"{executableName} is required by the {packageManager.Name} workflow but was not found on PATH.", $"Install {executableName} {baseline} or a compatible newer release."));
            return null;
        }
        CommandResult result = await runtime.RunAsync(executable, project.ProjectDirectory, ["--version"], cancellationToken).ConfigureAwait(false);
        string version = result.StandardOutput.Trim().TrimStart('v');
        if (result.ExitCode != 0 || !IsCompatibleToolVersion(version, baseline))
        {
            checks.Add(Fail("javascript-runtime", $"{executableName} {version} is outside the supported range beginning at {baseline}.", $"Install {executableName} {baseline} or a newer release in the same major version."));
            return null;
        }
        checks.Add(StringComparer.Ordinal.Equals(version, baseline)
            ? Pass("javascript-runtime", $"{executableName} {version} matches certified baseline {Authority.Id}.")
            : Warn("javascript-runtime", $"{executableName} {version} is supported; certification used {baseline}.", "No change is required."));
        return executable;
    }

    private static async Task CheckPackageManagerAsync(List<DoctorCheck> checks,
        DoctorProjectConfiguration project, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        JavaScriptPackageManager packageManager;
        try { packageManager = JavaScriptPackageManager.Resolve(project.FrontendPackageDirectory, project.FrontendPackageDirectory); }
        catch (DevUsageException error)
        {
            checks.Add(Fail("package-manager", error.Message, "Choose npm, pnpm, or Bun and commit its matching lock file."));
            return;
        }
        string? executable = runtime.FindExecutable(packageManager.Executable);
        string baseline = packageManager.Name switch
        {
            "npm" => Authority.Toolchain.Npm,
            "pnpm" => Authority.Toolchain.Pnpm,
            "bun" => Authority.Toolchain.Bun,
            _ => throw new InvalidOperationException(),
        };
        if (executable is null)
        {
            checks.Add(Fail("package-manager", $"The workspace selects {packageManager.Name}, but '{packageManager.Executable}' is unavailable.", $"Install {packageManager.Name} {baseline} and put it on PATH."));
        }
        else
        {
            CommandResult result = await runtime.RunAsync(executable, project.FrontendPackageDirectory, ["--version"], cancellationToken).ConfigureAwait(false);
            string version = result.StandardOutput.Trim().TrimStart('v');
            string? declared = JavaScriptPackageManager.ReadDeclaredVersion(Path.Combine(project.FrontendPackageDirectory, "package.json"));
            if (result.ExitCode != 0 || !IsCompatibleToolVersion(version, baseline))
            {
                checks.Add(Fail("package-manager", $"{packageManager.Name} {version} is outside the supported range beginning at {baseline}.", $"Activate {packageManager.Name} {baseline}."));
            }
            else if (!StringComparer.Ordinal.Equals(version, baseline) || (declared is not null && !StringComparer.Ordinal.Equals(version, declared)))
            {
                checks.Add(Warn("package-manager", $"{packageManager.Name} {version} is supported; certified baseline is {baseline}.", declared is null ? "No change is required." : $"Activate packageManager-declared {packageManager.Name} {declared} for reproducible results."));
            }
            else
            {
                checks.Add(Pass("package-manager", $"{packageManager.Name} {version} matches certified baseline."));
            }
        }
        string lockPath = Path.Combine(project.FrontendPackageDirectory, packageManager.LockFileName);
        checks.Add(File.Exists(lockPath)
            ? Pass("lock-file", $"Found reproducible lock file '{packageManager.LockFileName}'.")
            : Fail("lock-file", $"The workspace has no '{packageManager.LockFileName}'.", $"Run {packageManager.Name} and commit its lock file."));
    }

    private static void CheckCompatibilitySet(List<DoctorCheck> checks, DoctorProjectConfiguration project)
    {
        if (!File.Exists(project.ProjectAssetsFile))
        {
            // A freshly generated project has not been restored yet. That is
            // expected: dotnet runic dev and dotnet build restore it.
            checks.Add(Warn("compatibility-set", "The project has not been restored yet, so Runic package versions were not checked.",
                "Run 'dotnet runic dev' or 'dotnet restore', then rerun doctor to check package versions."));
            return;
        }
        var mismatches = new List<string>();
        int selected = 0;
        try
        {
            using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(project.ProjectAssetsFile));
            if (!assets.RootElement.TryGetProperty("libraries", out JsonElement libraries))
            {
                checks.Add(Fail("compatibility-set", "NuGet restore graph has no libraries."));
                return;
            }
            bool hasViewsHost = false;
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                int separator = library.Name.LastIndexOf('/');
                if (separator <= 0) continue;
                string identity = library.Name[..separator];
                string version = library.Name[(separator + 1)..];
                string type = library.Value.TryGetProperty("type", out JsonElement typeNode) ? typeNode.GetString() ?? string.Empty : string.Empty;
                if (identity is "Runic.Application.CsWebUi" or "Runic.Application.Desktop") hasViewsHost = true;
                if (Authority.NuGetPackages.TryGetValue(identity, out CompatibilityPackage? expected))
                {
                    selected++;
                    if (type == "package" && !StringComparer.Ordinal.Equals(version, expected.Version)) mismatches.Add($"{identity} {version} (expected {expected.Version})");
                }
                else if (type == "package" && IsRunicIdentity(identity) && !IsIndependentlyReleasedNuGetIdentity(identity))
                    mismatches.Add($"{identity} {version} (not selected by {Authority.Id})");
            }
            if (!hasViewsHost) mismatches.Add("a Runic Views host package (Runic.Application.CsWebUi or Runic.Application.Desktop) is missing");
        }
        catch (JsonException error)
        {
            checks.Add(Fail("compatibility-set", $"NuGet restore graph could not be read: {Compact(error.Message)}", "Restore the project and rerun doctor."));
            return;
        }

        string manifestPath = Path.Combine(project.FrontendPackageDirectory, "package.json");
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            foreach (string sectionName in new[] { "dependencies", "devDependencies", "optionalDependencies" })
            {
                if (!manifest.RootElement.TryGetProperty(sectionName, out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty dependency in dependencies.EnumerateObject())
                {
                    if (!dependency.Name.StartsWith("@runic-artifex/", StringComparison.Ordinal)) continue;
                    string version = dependency.Value.GetString() ?? string.Empty;
                    if (Authority.NpmPackages.TryGetValue(dependency.Name, out CompatibilityPackage? expected))
                    {
                        selected++;
                        if (!StringComparer.Ordinal.Equals(version, expected.Version)) mismatches.Add($"{dependency.Name} {version} (expected {expected.Version})");
                    }
                    else if (!IsIndependentlyReleasedNpmIdentity(dependency.Name))
                        mismatches.Add($"{dependency.Name} (not selected by {Authority.Id})");
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            mismatches.Add("frontend package.json is missing or unreadable");
        }

        if (mismatches.Count != 0)
        {
            checks.Add(Fail("compatibility-set", $"Compatibility set {Authority.Id} does not match: {string.Join("; ", mismatches)}.", $"Select the exact package versions recorded by {Authority.Id}, restore, and rerun doctor."));
            return;
        }
        checks.Add(selected > 0
            ? Pass("compatibility-set", $"{selected} Runic package(s) match {Authority.Id} ({Authority.ReleaseTrainVersion}).")
            : Warn("compatibility-set", $"No Runic package was selected by {Authority.Id}.", "Restore the generated Views Window project."));
    }

    private static void CheckFrontendDevelopment(List<DoctorCheck> checks, DoctorProjectConfiguration project)
    {
        if (!project.ViteDevServerEnabled)
        {
            checks.Add(Pass("frontend-dev-server", "The project uses its configured frontend dev script."));
            return;
        }
        if (project.ViteConfigurationPath.Length == 0 || !File.Exists(project.ViteConfigurationPath))
        {
            checks.Add(Fail("vite-config", "The configured Vite configuration file is missing.", "Create vite.config.ts or correct its project property."));
            return;
        }
        string entry = project.ViteDevServerEntry;
        string entryPath = Path.GetFullPath(Path.Combine(project.FrontendPackageDirectory, entry.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (entry.Length == 0 || entry[0] != '/' || entry.StartsWith("//", StringComparison.Ordinal) || !File.Exists(entryPath))
        {
            checks.Add(Fail("vite-entry", "The configured Vite entry module is missing or invalid.", "Set the entry to an existing root-relative module path."));
            return;
        }
        checks.Add(Pass("vite-config", $"Found Vite configuration and entry module '{entry}'."));
    }

    private static void CheckBrowser(List<DoctorCheck> checks, DoctorProjectConfiguration project,
        IDoctorRuntime runtime)
    {
        if (project.Host == RunicViewsHost.Desktop)
        {
            checks.Add(Pass("browser", "Not required: Runic Desktop renders the app in its own native window."));
            return;
        }
        string? configured = runtime.GetEnvironmentVariable("RUNIC_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured);
            checks.Add(File.Exists(path)
                ? Pass("browser", $"Found configured browser at '{path}'.")
                : Fail("browser", "RUNIC_BROWSER_PATH points to a missing file.", "Set it to an installed Chromium-family browser or unset it."));
            return;
        }
        string? browser = FindBrowser(runtime);
        checks.Add(browser is not null
            ? Pass("browser", $"Found browser '{browser}'.")
            : Warn("browser", "No Chromium-family browser was found on PATH.", project.Host == RunicViewsHost.CsWebUi
                ? "CS-WebUI opens the app in an installed browser, preferring Chromium-family browsers, and falls back to the platform WebView. Install Chrome, Edge or Chromium, or set RUNIC_BROWSER_PATH."
                : $"The project references neither {DoctorProjectConfiguration.CsWebUiPackage} nor {DoctorProjectConfiguration.DesktopPackage}. A browser-based host prefers an installed Chromium-family browser; install Chrome, Edge or Chromium, or set RUNIC_BROWSER_PATH."));
    }

    /// <summary>Finds a Chromium-family browser on this machine.</summary>
    internal static string? FindBrowser(IDoctorRuntime runtime)
    {
        string[] candidates = runtime.Platform.Os switch
        {
            "win" => ["msedge", "chrome", "chromium"],
            "osx" => ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"],
            _ => ["chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "microsoft-edge"],
        };
        return candidates.Select(runtime.FindExecutable).FirstOrDefault(static path => path is not null);
    }

    private static bool IsRunicIdentity(string identity) =>
        identity.StartsWith("Runic", StringComparison.OrdinalIgnoreCase) ||
        identity.StartsWith("dotnet-runic", StringComparison.OrdinalIgnoreCase);

    private static bool IsIndependentlyReleasedNuGetIdentity(string identity) =>
        identity.StartsWith("Runic.CommandLine", StringComparison.OrdinalIgnoreCase) ||
        identity.StartsWith("Runic.Translations", StringComparison.OrdinalIgnoreCase) ||
        StringComparer.OrdinalIgnoreCase.Equals(identity, "dotnet-runic-translations");

    private static bool IsIndependentlyReleasedNpmIdentity(string identity) =>
        StringComparer.Ordinal.Equals(identity, "@runic-artifex/vite-plugin-runic-translations") ||
        identity.StartsWith("@runic-artifex/translations", StringComparison.Ordinal);

    private static int? ParseTargetFrameworkMajor(string targetFramework)
    {
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)) return null;
        string value = targetFramework[3..];
        int separator = value.IndexOf('.');
        if (separator >= 0) value = value[..separator];
        return int.TryParse(value, out int major) ? major : null;
    }

    private static string NormalizeVersion(string version)
    {
        int separator = version.IndexOfAny(['-', '+']);
        return separator > 0 ? version[..separator] : version;
    }

    private static bool IsCompatibleToolVersion(string actual, string baseline) =>
        Version.TryParse(NormalizeVersion(actual), out Version? actualVersion) &&
        Version.TryParse(NormalizeVersion(baseline), out Version? baselineVersion) &&
        actualVersion.Major == baselineVersion.Major && actualVersion >= baselineVersion;

    private static string Compact(string text) => text.Length <= 300 ? text : text[..300] + "…";
    private static DoctorCheck Pass(string name, string message) => new(DoctorStatus.Pass, name, message);
    private static DoctorCheck Warn(string name, string message, string remediation) => new(DoctorStatus.Warning, name, message, remediation);
    private static DoctorCheck Fail(string name, string message, string? remediation = null) => new(DoctorStatus.Failure, name, message, remediation);
}
