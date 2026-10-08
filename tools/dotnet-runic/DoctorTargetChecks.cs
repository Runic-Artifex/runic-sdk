using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop.Internal;

namespace Runic.Application.Tool;

/// <summary>The operating system and architecture doctor runs on.</summary>
/// <param name="Os">win, linux, osx, or another lowercase name.</param>
/// <param name="Architecture">x64, arm64, or another lowercase name.</param>
/// <param name="Musl">True on a musl-based Linux distribution.</param>
/// <param name="OsVersion">The operating system version, for messages only.</param>
internal sealed record DoctorHostPlatform(string Os, string Architecture, bool Musl, string OsVersion);

/// <summary>A parsed portable runtime identifier such as linux-x64 or linux-musl-arm64.</summary>
internal sealed partial record DoctorTargetRid(string Value, string Os, string Architecture, bool Musl)
{
    // Portable operating-system names from the .NET RID catalog. Anything else
    // (win10, ubuntu.22.04, ...) is a version-specific RID the SDK no longer uses.
    private static readonly HashSet<string> PortableOperatingSystems = new(StringComparer.Ordinal)
    {
        "win", "linux", "linux-musl", "linux-bionic", "osx", "maccatalyst", "ios", "iossimulator",
        "tvos", "tvossimulator", "android", "browser", "wasi", "freebsd", "illumos", "solaris", "haiku",
    };

    internal const string UsageCode = "RAPPCLI1011";

    /// <summary>The OS family a Runic host build uses: win, linux (including musl) or osx.</summary>
    internal string Family => Musl ? "linux" : Os;

    internal bool IsPortable => PortableOperatingSystems.Contains(Os);

    internal static DoctorTargetRid Parse(string value)
    {
        if (!RidSyntax().IsMatch(value))
        {
            throw new DevUsageException(UsageCode,
                "--rid must be a runtime identifier such as linux-x64, win-x64 or osx-arm64.");
        }
        int separator = value.LastIndexOf('-');
        string os = value[..separator];
        return new DoctorTargetRid(value, os, value[(separator + 1)..], os == "linux-musl");
    }

    [GeneratedRegex("^[a-z0-9]+(?:\\.[0-9]+)*(?:-[a-z0-9]+)+$")]
    private static partial Regex RidSyntax();
}

/// <summary>
/// Deployment checks for <c>doctor --rid</c>. They describe what publishing for
/// the target needs on this machine and what the target machines need at run
/// time. Requirements of another operating system cannot be inspected from this
/// host and are reported as passing information.
/// </summary>
internal static class DoctorTargetChecks
{
    // Host and RID support comes from eng/support.json through the embedded compatibility set.
    private static SupportMatrix Support => CompatibilitySetAuthority.Current.Support;

    private const string Gtk3Library = "libgtk-3.so.0";
    private const string WebKit41Library = "libwebkit2gtk-4.1.so.0";
    internal static readonly Version MinimumGtk4 =
        Version.TryParse(Support.Requirement("gtk4")?.Minimum, out Version? minimum) ? minimum : new(4, 12);

    internal static async Task InspectAsync(
        List<DoctorCheck> checks,
        DoctorProjectConfiguration project,
        DoctorTargetRid target,
        IDoctorRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!CheckRid(checks, project, target)) return;
        CheckRuntimeIdentifiers(checks, project, target);
        await CheckPublishAsync(checks, project, target, runtime, cancellationToken).ConfigureAwait(false);
        await CheckPresentationAsync(checks, project, target, runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns false when the target cannot host a Runic Views Window at all.</summary>
    private static bool CheckRid(List<DoctorCheck> checks, DoctorProjectConfiguration project, DoctorTargetRid target)
    {
        const string id = "target-rid";
        if (!target.IsPortable)
        {
            checks.Add(Fail(id, $"'{target.Value}' is not a portable runtime identifier.",
                "Use a portable RID such as linux-x64, linux-musl-x64, win-x64 or osx-arm64."));
            return false;
        }
        SupportTarget? support = FindSupport(project.Host, target.Value);
        string assumed = project.Host == RunicViewsHost.Unknown
            ? " The project references no Runic Views host, so doctor assumed the most permissive host."
            : string.Empty;
        switch (support?.Status)
        {
            case SupportStatus.CiVerified:
                checks.Add(Pass(id, $"{target.Value} is CI-verified: {Support.CiVerifiedMeaning}{assumed}"));
                return true;
            case SupportStatus.PackagedUnverified:
                checks.Add(Warn(id, $"{target.Value} has native support but is not verified by Runic CI. {support.Reason}{assumed}",
                    "Run the published application on a real target machine before you ship it."));
                return true;
            case SupportStatus.Unsupported:
                checks.Add(Fail(id, support.Reason, support.Remediation ?? SupportedRidsRemediation(project.Host)));
                return false;
            default:
                checks.Add(Fail(id, $"Runic Views Window hosts do not support {target.Value}.", SupportedRidsRemediation(project.Host)));
                return false;
        }
    }

    // The support entries of the project's host; a project without a known host may use any host.
    private static IEnumerable<SupportTarget> HostTargets(RunicViewsHost host)
    {
        string? id = host switch
        {
            RunicViewsHost.Desktop => "desktop",
            RunicViewsHost.CsWebUi => "cswebui",
            _ => null,
        };
        return Support.Hosts.Where(candidate => id is null || candidate.Id == id).SelectMany(candidate => candidate.Targets);
    }

    private static SupportTarget? FindSupport(RunicViewsHost host, string rid) =>
        HostTargets(host).Where(entry => entry.Rid == rid).OrderByDescending(entry => entry.Status).FirstOrDefault();

    private static string SupportedRidsRemediation(RunicViewsHost host)
    {
        string[] verified = [.. HostTargets(host)
            .Where(entry => entry.Status == SupportStatus.CiVerified).Select(entry => entry.Rid).Distinct()];
        return verified.Length == 0
            ? "See the supported platforms in the Runic SDK README."
            : $"Publish for {string.Join(", ", verified)}, which Runic CI verifies.";
    }

    private static void CheckRuntimeIdentifiers(List<DoctorCheck> checks, DoctorProjectConfiguration project, DoctorTargetRid target)
    {
        const string id = "target-runtime-identifiers";
        string[] declared = project.RuntimeIdentifiers.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (declared.Length == 0)
        {
            checks.Add(Pass(id, $"The project declares no RuntimeIdentifiers; 'dotnet publish -r {target.Value}' restores the target."));
            return;
        }
        checks.Add(declared.Contains(target.Value, StringComparer.Ordinal)
            ? Pass(id, $"RuntimeIdentifiers includes {target.Value}.")
            : Warn(id, $"RuntimeIdentifiers ({string.Join(", ", declared)}) does not include {target.Value}.",
                $"Add {target.Value} to RuntimeIdentifiers, or publish without --no-restore so restore adds the target."));
    }

    private static async Task CheckPublishAsync(List<DoctorCheck> checks, DoctorProjectConfiguration project,
        DoctorTargetRid target, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        const string id = "target-publish";
        if (!project.PublishAot)
        {
            string version = TargetFrameworkMajor(project.TargetFramework) is { Length: > 0 } major ? $" {major}" : string.Empty;
            checks.Add(Pass(id, project.SelfContained
                ? $"The project publishes a self-contained {target.Value} application without Native AOT; target machines need no .NET installation."
                : project.Host == RunicViewsHost.Desktop
                    ? $"The project publishes framework-dependent for {target.Value}; target machines need the .NET{version} and ASP.NET Core{version} runtimes."
                    : $"The project publishes framework-dependent for {target.Value}; target machines need the .NET{version} runtime."));
            return;
        }

        DoctorHostPlatform host = runtime.Platform;
        if (host.Os != target.Family)
        {
            checks.Add(Fail(id, $"Native AOT cannot compile {target.Value} on a {DescribeOs(host.Os)} machine; it does not cross operating systems.",
                $"Publish on a {DescribeOs(target.Family)} machine or CI runner, or publish {target.Value} with -p:PublishAot=false."));
            return;
        }

        switch (host.Os)
        {
            case "linux":
                bool crossArchitecture = host.Architecture != target.Architecture;
                bool crossLibc = host.Musl != target.Musl;
                string? compiler = runtime.FindExecutable("clang") ?? runtime.FindExecutable("gcc");
                bool objcopy = !project.StripSymbols ||
                    runtime.FindExecutable("objcopy") is not null || runtime.FindExecutable("llvm-objcopy") is not null;
                if (compiler is null || !objcopy)
                {
                    checks.Add(Fail(id, compiler is null
                            ? "Native AOT needs clang or gcc on PATH to link the executable."
                            : "Native AOT needs objcopy or llvm-objcopy on PATH to strip symbols into a separate file.",
                        "Install clang, binutils (objcopy) and the zlib development package (for example clang, binutils and zlib1g-dev), or set StripSymbols=false."));
                }
                else if (crossArchitecture || crossLibc)
                {
                    checks.Add(Warn(id, $"Native AOT can compile {target.Value} on this {host.Architecture}{(host.Musl ? " musl" : string.Empty)} machine only with a target sysroot.",
                        $"Publish on a {target.Value} machine, or provide a {target.Value} sysroot and cross linker (see the .NET Native AOT cross-compilation guide)."));
                }
                else
                {
                    checks.Add(Pass(id, $"Native AOT can compile {target.Value} here with {Path.GetFileName(compiler)}{(project.StripSymbols ? " and objcopy" : string.Empty)}; it also needs the zlib development package."));
                }
                return;
            case "osx":
                checks.Add(runtime.FindExecutable("xcrun") is not null
                    ? Pass(id, $"Native AOT can compile {target.Value} with the Xcode command-line tools.")
                    : Fail(id, "Native AOT needs the Xcode command-line tools, and xcrun was not found.",
                        "Run 'xcode-select --install'."));
                return;
            case "win":
                string component = target.Architecture == "arm64"
                    ? "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
                    : "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
                bool found = await HasVisualStudioComponentAsync(project, runtime, component, cancellationToken).ConfigureAwait(false);
                checks.Add(found
                    ? Pass(id, $"Native AOT can compile {target.Value} with the Visual Studio C++ tools.")
                    : Fail(id, $"Native AOT needs the Visual Studio C++ build tools for {target.Architecture}, which were not found.",
                        $"Install the 'Desktop development with C++' workload with the {component} component."));
                return;
            default:
                checks.Add(Fail(id, $"Native AOT is not supported on a {host.Os} machine.", "Publish on a Windows, Linux or macOS machine."));
                return;
        }
    }

    private static async Task<bool> HasVisualStudioComponentAsync(DoctorProjectConfiguration project,
        IDoctorRuntime runtime, string component, CancellationToken cancellationToken)
    {
        string? programFiles = runtime.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        string? vswhere = runtime.FindExecutable(Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe"));
        if (vswhere is null) return false;
        CommandResult result = await runtime.RunAsync(vswhere, project.ProjectDirectory,
            ["-latest", "-prerelease", "-products", "*", "-requires", component, "-property", "installationPath"],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private static async Task CheckPresentationAsync(List<DoctorCheck> checks, DoctorProjectConfiguration project,
        DoctorTargetRid target, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        const string id = "target-presentation";
        string webView = WebViewRequirement(target, project.UsesGtk4);
        bool sameOs = runtime.Platform.Os == target.Family;
        switch (project.Host)
        {
            case RunicViewsHost.Desktop:
            {
                string requirement = $"Runic Desktop's embedded window on {target.Value} needs {webView}.";
                if (!sameOs)
                {
                    checks.Add(Pass(id, $"{requirement} A {DescribeOs(runtime.Platform.Os)} machine cannot inspect a {DescribeOs(target.Family)} target."));
                    return;
                }
                WebViewProbe probe = await ProbeWebViewAsync(project, target, runtime, cancellationToken).ConfigureAwait(false);
                checks.Add(new DoctorCheck(probe.Status, id, $"{requirement} {probe.Message}", probe.Remediation));
                return;
            }
            case RunicViewsHost.CsWebUi:
            {
                string requirement = $"CS-WebUI on {target.Value} opens an installed browser, preferring Chromium-family browsers, and falls back to {webView}.";
                if (!sameOs)
                {
                    checks.Add(Pass(id, $"{requirement} A {DescribeOs(runtime.Platform.Os)} machine cannot inspect a {DescribeOs(target.Family)} target."));
                    return;
                }
                if (DoctorChecks.FindBrowser(runtime) is not null)
                {
                    checks.Add(Pass(id, $"{requirement} This machine has a Chromium-family browser."));
                    return;
                }
                WebViewProbe probe = await ProbeWebViewAsync(project, target, runtime, cancellationToken).ConfigureAwait(false);
                checks.Add(probe.Status == DoctorStatus.Pass
                    ? Pass(id, $"{requirement} This machine has no Chromium-family browser on PATH; {probe.Message}")
                    : Warn(id, $"{requirement} This machine has neither a Chromium-family browser on PATH nor the platform WebView.",
                        "Install Chrome, Edge or Chromium on target machines, or the platform WebView. " + probe.Remediation));
                return;
            }
            default:
                checks.Add(Warn(id, "The project references no Runic Views host, so the target's presentation requirements are unknown.",
                    $"Reference {DoctorProjectConfiguration.DesktopPackage} or {DoctorProjectConfiguration.CsWebUiPackage}."));
                return;
        }
    }

    private sealed record WebViewProbe(DoctorStatus Status, string Message, string? Remediation = null);

    private static async Task<WebViewProbe> ProbeWebViewAsync(DoctorProjectConfiguration project,
        DoctorTargetRid target, IDoctorRuntime runtime, CancellationToken cancellationToken)
    {
        switch (target.Family)
        {
            case "win":
                string? version = runtime.GetWebView2RuntimeVersion();
                return version is not null
                    ? new(DoctorStatus.Pass, $"This machine has WebView2 Runtime {version}.")
                    : new(DoctorStatus.Warning, "This machine has no Microsoft Edge WebView2 Runtime.",
                        "Install the evergreen WebView2 Runtime here, and on target machines that lack it (Windows 11 includes it).");
            case "osx":
                return new(DoctorStatus.Pass, $"WKWebView is part of macOS; this machine runs macOS {runtime.Platform.OsVersion}.");
            default:
                string[] gtk = project.UsesGtk4 ? [Gtk4NativeLibraries.Gtk] : [Gtk3Library];
                string[] webkit = project.UsesGtk4 ? Gtk4NativeLibraries.WebKit : [WebKit41Library];
                var missing = new List<string>();
                foreach (string[] names in new[] { gtk, webkit })
                {
                    bool found = false;
                    foreach (string library in names)
                    {
                        if (found = await runtime.IsNativeLibraryAvailableAsync(library, cancellationToken).ConfigureAwait(false)) break;
                    }
                    if (!found) missing.Add(names[0]);
                }
                if (missing.Count != 0)
                {
                    return new(DoctorStatus.Warning, $"This machine is missing {string.Join(" and ", missing)}.",
                        project.UsesGtk4
                            ? Capitalize(DoctorGtk4Packages.Remediation(DoctorGtk4Packages.FromOsRelease(runtime.OsReleasePath), MinimumGtk4,
                                gtk4: missing.Contains(gtk[0]), webKit6: missing.Contains(webkit[0]))) + " on this machine and on target machines."
                            : "Install GTK 3 and WebKitGTK 4.1 (for example libgtk-3-0 and libwebkit2gtk-4.1-0) on this machine and on target machines.");
                }
                if (project.UsesGtk4)
                {
                    Version? gtkVersion = await ReadPkgConfigVersionAsync(project, runtime, "gtk4", cancellationToken).ConfigureAwait(false);
                    if (gtkVersion is not null && gtkVersion < MinimumGtk4)
                    {
                        return new(DoctorStatus.Warning, $"This machine has GTK {gtkVersion}, older than the required {MinimumGtk4}.",
                            $"Install GTK {MinimumGtk4} or newer on this machine and on target machines.");
                    }
                    return new(DoctorStatus.Pass, gtkVersion is null
                        ? "This machine has the GTK 4 and WebKitGTK 6.0 libraries."
                        : $"This machine has GTK {gtkVersion} and WebKitGTK 6.0.");
                }
                return new(DoctorStatus.Pass, "This machine has the GTK 3 and WebKitGTK 4.1 libraries.");
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    internal static async Task<Version?> ReadPkgConfigVersionAsync(DoctorProjectConfiguration project,
        IDoctorRuntime runtime, string module, CancellationToken cancellationToken)
    {
        // pkg-config is optional: a machine with only the runtime libraries has none.
        string? pkgConfig = runtime.FindExecutable("pkg-config");
        if (pkgConfig is null) return null;
        CommandResult result = await runtime.RunAsync(pkgConfig, project.ProjectDirectory, ["--modversion", module], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && Version.TryParse(result.StandardOutput.Trim(), out Version? version) ? version : null;
    }

    private static string WebViewRequirement(DoctorTargetRid target, bool gtk4) =>
        target.Family switch
        {
            "win" => "the Microsoft Edge WebView2 Runtime",
            "osx" => "WKWebView, which macOS includes",
            _ => gtk4 ? $"GTK {MinimumGtk4} or newer with WebKitGTK 6.0" : "GTK 3 with WebKitGTK 4.1",
        };

    private static string TargetFrameworkMajor(string targetFramework)
    {
        string value = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? targetFramework[3..] : targetFramework;
        int separator = value.IndexOfAny(['.', '-']);
        return separator > 0 ? value[..separator] : value;
    }

    private static string DescribeOs(string os) =>
        os switch
        {
            "win" => "Windows",
            "linux" => "Linux",
            "osx" => "macOS",
            _ => os,
        };

    private static DoctorCheck Pass(string name, string message) => new(DoctorStatus.Pass, name, message);
    private static DoctorCheck Warn(string name, string message, string remediation) => new(DoctorStatus.Warning, name, message, remediation);
    private static DoctorCheck Fail(string name, string message, string remediation) => new(DoctorStatus.Failure, name, message, remediation);
}
