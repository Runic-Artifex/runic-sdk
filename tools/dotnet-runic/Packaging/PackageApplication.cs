using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>What one target run needs: the options, the identity, the resolved runtimes and the output directory.</summary>
internal sealed class PackageContext(PackageOptions options, PackageIdentity identity, IReadOnlyList<string> runtimes, string output, string dotnet)
{
    internal PackageOptions Options { get; } = options;
    internal PackageIdentity Identity { get; } = identity;
    internal IReadOnlyList<string> Runtimes { get; } = runtimes;
    internal string Runtime => Runtimes[0];
    internal string Output { get; } = output;
    internal string DotNet { get; } = dotnet;
    internal List<PackageCommandPackage> Packages { get; } = [];
    internal List<string> Warnings { get; } = [];

    /// <summary>Reports a warning in the output and the payload.</summary>
    internal void Warn(string message)
    {
        Warnings.Add(message);
        Console.Out.WriteLine("warning: " + message);
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
                "-p:PublishDocumentationFiles=false", "-p:PublishReferencesDocumentationFiles=false", "-o", directory],
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
        : throw new DevUsageException(PackageCodes.IdentityInvalid,
            "The package icon does not exist. Add a 512 px icons/icon.png or set RunicPackageIcon.", $"Icon: {Identity.Icon}\n");
}

/// <summary><c>dotnet runic package &lt;target&gt;</c>: resolves the project and runtime, then runs the target.</summary>
internal static class PackageApplication
{
    internal static async Task<PackageCommandResult> RunAsync(PackageOptions options, CancellationToken cancellationToken)
    {
        string target = PackageOptions.Name(options.Target);
        IReadOnlyList<string> runtimes = ResolveRuntimes(options);
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string dotnet = DevApplication.ResolveDotNetHost();
        PackageIdentity identity = await PackageIdentity.ReadAsync(dotnet, project, options.Configuration, options.Publisher, cancellationToken)
            .ConfigureAwait(false);
        string output = Path.GetFullPath(options.OutputDirectory is { Length: > 0 } selected
            ? Path.GetFullPath(selected, Environment.CurrentDirectory)
            : Path.Combine(identity.ProjectDirectory, "artifacts", "package", target));
        Directory.CreateDirectory(output);
        var context = new PackageContext(options, identity, runtimes, output, dotnet);
        Func<PackageContext, CancellationToken, Task> run = options.Target switch
        {
            PackageTarget.Windows => WindowsPackager.RunAsync,
            PackageTarget.MacOS => MacOSPackager.RunAsync,
            PackageTarget.Flatpak => FlatpakPackager.RunAsync,
            PackageTarget.AppImage => AppImagePackager.RunAsync,
            PackageTarget.Deb => DebPackager.RunAsync,
            PackageTarget.Rpm => RpmPackager.RunAsync,
            _ => throw new DevUsageException(PackageCodes.WrongOperatingSystem, $"The {target} target is not available in this version."),
        };
        await run(context, cancellationToken).ConfigureAwait(false);
        foreach (PackageCommandPackage package in context.Packages)
            Console.Out.WriteLine($"Packaged {identity.Id} {identity.Version}: {package.Path} ({package.Format}, signing: {package.Signing})");
        return new PackageCommandResult(target, new(identity.Id, identity.Version), context.Packages, context.Warnings);
    }

    /// <summary>
    /// Checks that this OS builds the target and that each runtime is the target's OS. Without <c>--runtime</c>, the
    /// host's architecture.
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
        if (runtimes.Count > 1 && options.Target != PackageTarget.MacOS)
            throw new DevUsageException(PackageCodes.WrongOperatingSystem, $"package {target} takes one runtime identifier; only macos combines several into a universal bundle.");
        foreach (string runtime in runtimes)
        {
            DoctorTargetRid rid = DoctorTargetRid.Parse(runtime);
            if (rid.Os != family)
            {
                throw new DevUsageException(PackageCodes.WrongOperatingSystem,
                    $"package {target} publishes for {PackageOptions.FamilyName(family)}; --runtime {runtime} is for another operating system.");
            }
        }
        return runtimes;
    }
}
