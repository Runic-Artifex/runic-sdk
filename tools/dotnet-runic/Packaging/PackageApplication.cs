using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>What one target run needs: the options, the identity, the resolved runtimes and the output directory.</summary>
internal sealed class PackageContext(PackageOptions options, PackageIdentity identity, IReadOnlyList<string> runtimes, string output, string dotnet,
    string? frontendBundle = null)
{
    /// <summary>The full path of <c>--frontend-bundle</c>'s web bundle, or null when the publish builds the frontends.</summary>
    internal string? FrontendBundle { get; } = frontendBundle;

    internal PackageOptions Options { get; } = options;
    internal PackageIdentity Identity { get; } = identity;
    internal IReadOnlyList<string> Runtimes { get; } = runtimes;
    internal string Runtime => Runtimes[0];
    internal string Output { get; } = output;
    internal string DotNet { get; } = dotnet;
    internal List<PackageCommandPackage> Packages { get; } = [];
    internal List<PackageCommandWarning> Warnings { get; } = [];

    /// <summary>Reports a warning, one of the <c>RAPPPKG2xxx</c> codes of <see cref="PackageCodes"/>, in the output and the payload.</summary>
    internal void Warn(string code, string message)
    {
        Warnings.Add(new(code, message));
        Console.Out.WriteLine($"warning {code}: {message}");
    }

    internal void Add(string path, string format, string signing) => Packages.Add(new(path, format, signing));

    /// <summary>
    /// Publishes the project with NativeAOT for the runtime into a new directory, without documentation files or debug
    /// symbols: keep the symbols per release from a publish of your own for crash analysis.
    /// </summary>
    internal async Task PublishAsync(string runtime, string directory, CancellationToken cancellationToken)
    {
        PackageTools.Delete(directory);
        await PackageTools.RunAsync(DotNet,
            ["publish", Identity.ProjectPath, "--nologo", "-c", Options.Configuration, "-r", runtime, "-p:PublishAot=true",
                "-p:PublishDocumentationFiles=false", "-p:PublishReferencesDocumentationFiles=false",
                .. Options.PropertyArguments(FrontendBundle), "-o", directory],
            Identity.ProjectDirectory, cancellationToken, PackageCodes.PublishFailed).ConfigureAwait(false);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dbg", StringComparison.Ordinal)
                || name.EndsWith(".dSYM", StringComparison.Ordinal))
            {
                PackageTools.Delete(entry);
            }
        }
    }

    /// <summary>The PNG icon, <c>RunicPackageIcon</c>; a missing icon is an identity error.</summary>
    internal string RequireIcon() => File.Exists(Identity.Icon)
        ? Identity.Icon
        : throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
            "The package icon does not exist. Add a 512 px icons/icon.png or set RunicPackageIcon.", $"Icon: {Identity.Icon}\n");
}

/// <summary><c>dotnet runic package &lt;target&gt;</c>: resolves the project and runtime, then runs the target.</summary>
internal static class PackageApplication
{
    internal static async Task<PackageCommandResult> RunAsync(PackageOptions options, CancellationToken cancellationToken)
    {
        string target = PackageOptions.Name(options.Target);
        options = options with { Properties = PackageOptions.ParseProperties(options.PropertyValues) };
        IReadOnlyList<string> runtimes = ResolveRuntimes(options);
        string? frontendBundle = ResolveFrontendBundle(options.FrontendBundle, Environment.CurrentDirectory);
        if (options.Target == PackageTarget.Flathub) FlathubPackager.Validate(options, PackageOptions.CurrentFamily);
        string dotnet = DevApplication.ResolveDotNetHost();
        // From a solution root, the solution's one Window project, as dev and doctor select it (ide-run-debug K1).
        string project = await ProjectDiscovery.SelectProjectAsync(Environment.CurrentDirectory, options.Project, $"package {target}", dotnet,
            cancellationToken).ConfigureAwait(false);
        PackageIdentity identity = await PackageIdentity.ReadAsync(dotnet, project, options.Configuration, options.Publisher, cancellationToken,
            options.PropertyArguments(frontendBundle)).ConfigureAwait(false);
        string output = Path.GetFullPath(options.OutputDirectory is { Length: > 0 } selected
            ? Path.GetFullPath(selected, Environment.CurrentDirectory)
            : Path.Combine(identity.ProjectDirectory, "artifacts", "package", target));
        Directory.CreateDirectory(output);
        var context = new PackageContext(options, identity, runtimes, output, dotnet, frontendBundle);
        if (frontendBundle is not null) Console.Out.WriteLine($"Taking the pages from the web bundle {frontendBundle}: the publish builds no frontend.");
        Func<PackageContext, CancellationToken, Task> run = options.Target switch
        {
            PackageTarget.Windows => WindowsPackager.RunAsync,
            PackageTarget.MacOS => MacOSPackager.RunAsync,
            PackageTarget.Flatpak => FlatpakPackager.RunAsync,
            PackageTarget.AppImage => AppImagePackager.RunAsync,
            PackageTarget.Deb => DebPackager.RunAsync,
            PackageTarget.Rpm => RpmPackager.RunAsync,
            PackageTarget.Flathub => FlathubPackager.RunAsync,
            _ => throw new DevUsageException(PackageCodes.WrongOperatingSystem, $"The {target} target is not available in this version."),
        };
        await run(context, cancellationToken).ConfigureAwait(false);
        foreach (PackageCommandPackage package in context.Packages)
            Console.Out.WriteLine($"Packaged {identity.Id} {identity.Version}: {package.Path} ({package.Format}, signing: {package.Signing})");
        return new PackageCommandResult(target, new(identity.Id, identity.Version), context.Packages, context.Warnings);
    }

    /// <summary>
    /// The full path of <c>--frontend-bundle</c> (split CI F5), checked offline as <c>doctor --bundle</c> checks it, so a
    /// missing or truncated download fails before the publish; null without the option. The publish's own check then
    /// compares the View fingerprints (RUNICBRIDGE094) and the host (RUNICBRIDGE095).
    /// </summary>
    internal static string? ResolveFrontendBundle(string? frontendBundle, string workingDirectory)
    {
        if (frontendBundle is null) return null;
        string bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frontendBundle, workingDirectory));
        (_, IReadOnlyList<string> problems) = WebBundle.Read(bundle);
        if (problems.Count != 0)
        {
            throw new DevDevelopmentException(PackageCodes.PropertyInvalid,
                $"--frontend-bundle does not name a usable web bundle: {string.Join("; ", problems)}. Write it with 'dotnet runic bundle', and download the whole artifact.",
                $"Bundle: {bundle}\n");
        }
        return bundle;
    }

    /// <summary>
    /// Checks that this OS builds the target and that the one runtime is the target's OS and one Runic supports. Without
    /// <c>--runtime</c>, the host's architecture.
    /// </summary>
    internal static IReadOnlyList<string> ResolveRuntimes(PackageOptions options, string? currentFamily = null, string? currentArchitecture = null)
    {
        currentFamily ??= PackageOptions.CurrentFamily;
        currentArchitecture ??= PackageOptions.CurrentArchitecture;
        string target = PackageOptions.Name(options.Target);
        string? host = PackageOptions.HostFamily(options.Target);
        if (host is not null && host != currentFamily)
        {
            throw new DevUsageException(PackageCodes.WrongOperatingSystem,
                $"package {target} runs on {PackageOptions.FamilyName(host)}; this is {PackageOptions.FamilyName(currentFamily)}.");
        }
        string family = PackageOptions.RuntimeFamily(options.Target);
        IReadOnlyList<string> runtimes = options.Runtimes.Count != 0 ? options.Runtimes : [$"{family}-{currentArchitecture}"];
        if (runtimes.Count > 1)
        {
            throw new DevUsageException(PackageCodes.WrongOperatingSystem, options.Target == PackageTarget.MacOS
                ? $"package {target} takes one runtime identifier; Runic supports macOS on Apple silicon only and makes no universal bundle. Use --runtime osx-arm64."
                : $"package {target} takes one runtime identifier.");
        }
        string runtime = runtimes[0];
        DoctorTargetRid rid = DoctorTargetRid.Parse(runtime);
        if (rid.Os != family)
        {
            throw new DevUsageException(PackageCodes.WrongOperatingSystem,
                $"package {target} publishes for {PackageOptions.FamilyName(family)}; --runtime {runtime} is for another operating system.");
        }
        // A RID that every host lists as unsupported, such as osx-x64 (Intel Macs), is refused before anything is built.
        SupportTarget[] support = [.. CompatibilitySetAuthority.Current.Support.Hosts.SelectMany(host => host.Targets).Where(entry => entry.Rid == runtime)];
        if (support.Length != 0 && support.All(entry => entry.Status == SupportStatus.Unsupported))
        {
            throw new DevUsageException(PackageCodes.WrongOperatingSystem,
                $"package {target} does not publish for {runtime}: {support[0].Reason} {support[0].Remediation}".TrimEnd());
        }
        return runtimes;
    }
}
