using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>What the Flathub manifest builds: the project and frontend below the git root, and the pinned .NET runtime.</summary>
internal sealed record FlathubBuild(
    PackageIdentity Identity,
    string GitUrl,
    string? GitCommit,
    string? GitTag,
    string ProjectPath,
    string? FrontendPath,
    string RuntimeVersion,
    bool HasNpmSources);

/// <summary>
/// <c>package flathub</c>: a Flathub source-build manifest directory for a consumer's own submission (D032.2):
/// <c>&lt;id&gt;.yml</c> on the GNOME runtime with the .NET and Node SDK extensions, a git source at the given commit
/// or tag, <c>nuget-sources.json</c> and <c>npm-sources.json</c> from the committed lock files (D039.17), and the
/// desktop and metainfo files. <c>--build</c> then builds it as Flathub does: dependencies and sources first, then
/// offline.
/// </summary>
internal static class FlathubPackager
{
    /// <summary>The Flathub SDK extensions of .NET 10 and Node.js 24, which follow the GNOME SDK's branch.</summary>
    internal const string DotNetExtension = "org.freedesktop.Sdk.Extension.dotnet10";
    internal const string NodeExtension = "org.freedesktop.Sdk.Extension.node24";

    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        PackageOptions options = context.Options;
        string icon = context.RequireIcon();
        string root = FindGitRoot(identity.ProjectDirectory) ?? identity.ProjectDirectory;
        if (root == identity.ProjectDirectory && !Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            context.Warn(PackageCodes.NoGitCheckout, "The project is not in a git checkout; the manifest assumes that the repository's root is the project directory.");
        string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (Relative(icon).StartsWith("../", StringComparison.Ordinal))
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable, "RunicPackageIcon is outside the git repository, which the Flathub build checks out.");

        // The NativeAOT lock file and the frontend's package-lock.json are the inputs.
        AotEvaluation aot = await EvaluateAotAsync(context, cancellationToken).ConfigureAwait(false);
        string output = context.Output;
        foreach (string stale in new[] { "nuget-sources.json", "npm-sources.json" }) PackageTools.Delete(Path.Combine(output, stale));
        (IReadOnlyList<FlathubNuGetPackage> nuget, string packageRoot) = await CollectNuGetAsync(context, aot, cancellationToken).ConfigureAwait(false);
        string? frontend = FindFrontend(identity.ProjectDirectory, aot.FrontendDirectory);
        await File.WriteAllTextAsync(Path.Combine(output, "nuget-sources.json"),
            FlathubSources.NuGetSources(nuget, package => NuGetSha512(packageRoot, package)), cancellationToken).ConfigureAwait(false);
        if (frontend is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "npm-sources.json"),
                FlathubSources.NpmSources(await File.ReadAllTextAsync(Path.Combine(frontend, "package-lock.json"), cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        }

        // The desktop integration, filled as for the Flatpak target, beside the manifest.
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.Flatpak);
        string entry = LinuxPackaging.Write(Path.Combine(output, identity.Id + ".desktop"), PackageManifests.Render(identity, "linux/app.desktop", fields));
        string metainfo = LinuxPackaging.Write(Path.Combine(output, identity.Id + ".metainfo.xml"), PackageManifests.Render(identity, "linux/app.metainfo.xml", fields));
        if (fields["MimeInfo"].Length != 0) LinuxPackaging.Write(Path.Combine(output, identity.Id + ".mime.xml"), fields["MimeInfo"]);
        if (identity.DBusActivatable)
            LinuxPackaging.Write(Path.Combine(output, identity.Id + ".service"), PackageManifests.Render(identity, "linux/app.service", fields));
        var build = new FlathubBuild(identity, options.GitUrl!, Value(options.GitCommit), Value(options.GitTag),
            Relative(identity.ProjectPath), frontend is null ? null : Relative(frontend), aot.RuntimeVersion, frontend is not null);
        string manifest = LinuxPackaging.Write(Path.Combine(output, identity.Id + ".yml"),
            Manifest(build, Relative(icon), identity.ScalableIcon is { } svg ? Relative(svg) : null));
        await LinuxPackaging.ValidateDesktopEntryAsync(context, entry, required: options.Build, cancellationToken).ConfigureAwait(false);
        if (PackageTools.Find("appstreamcli") is { } appstreamcli)
            await PackageTools.RunAsync(appstreamcli, ["validate", "--no-net", "--explain", metainfo], output, cancellationToken, PackageCodes.ValidationFailed).ConfigureAwait(false);
        else if (options.Build) throw PackageTools.Missing("appstreamcli", "appstream");
        context.Add(manifest, "flathub-manifest", "none");

        if (options.Build) await BuildAsync(context, manifest, cancellationToken).ConfigureAwait(false);
        else context.Warn(PackageCodes.FlathubSubmission, "Flathub builds and signs the application from this manifest; submit it from your own account (docs/packaging.md#flathub).");
    }

    /// <summary>The options checked before the project is evaluated: one of the commit and the tag, and --build on Linux.</summary>
    internal static void Validate(PackageOptions options, string currentFamily)
    {
        if (string.IsNullOrWhiteSpace(options.GitCommit) == string.IsNullOrWhiteSpace(options.GitTag))
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                "package flathub needs the source's --git-commit or --git-tag (one of them).");
        }
        if (options.Build && currentFamily != "linux")
        {
            throw new DevUsageException(PackageCodes.WrongOperatingSystem,
                $"package flathub --build runs on Linux; this is {PackageOptions.FamilyName(currentFamily)}. Without --build it only writes the manifest.");
        }
    }

    private static string? Value(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// <c>--build</c>: installs the runtime, SDK and extensions, downloads every source, then builds without network
    /// access as Flathub's builders do, and writes a bundle to install.
    /// </summary>
    private static async Task BuildAsync(PackageContext context, string manifest, CancellationToken cancellationToken)
    {
        string builder = PackageTools.Require("flatpak-builder", "flatpak-builder");
        string flatpak = PackageTools.Require("flatpak", "flatpak");
        string output = context.Output;
        string state = $"--state-dir={Path.Combine(output, ".flatpak-builder")}";
        string build = Path.Combine(output, "build");
        await PackageTools.RunAsync(builder, ["--user", "--install-deps-from=flathub", "--install-deps-only", state, build, manifest], output, cancellationToken).ConfigureAwait(false);
        await PackageTools.RunAsync(builder, ["--download-only", state, build, manifest], output, cancellationToken).ConfigureAwait(false);
        string repository = Path.Combine(output, "repo");
        await PackageTools.RunAsync(builder, ["--user", "--disable-download", "--force-clean", "--disable-rofiles-fuse", state, $"--repo={repository}", build, manifest],
            output, cancellationToken).ConfigureAwait(false);
        string bundle = Path.Combine(output, context.Identity.Id + ".flatpak");
        await PackageTools.RunAsync(flatpak, ["build-bundle", repository, bundle, context.Identity.Id], output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(build);
        context.Add(bundle, "flatpak", "none");
    }

    private sealed record AotEvaluation(string LockFile, string RuntimeVersion, string FrontendDirectory, IReadOnlyList<(FlathubNuGetPackage Package, string ContentHash)> Locked);

    /// <summary>
    /// Reads the project's NativeAOT lock file (<c>NuGetLockFilePath</c> with <c>PublishAot=true</c>), which must list
    /// both Flathub architectures, and pins the .NET runtime to its NativeAOT compiler's version, so the Flathub SDK
    /// extension, which may be newer, restores the same packages.
    /// </summary>
    private static async Task<AotEvaluation> EvaluateAotAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        string[] missing = [.. FlathubSources.Runtimes.Where(runtime => !identity.RuntimeIdentifiers.Contains(runtime, StringComparer.Ordinal))];
        if (missing.Length > 0)
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                $"RuntimeIdentifiers must list {string.Join(" and ", FlathubSources.Runtimes)} for a Flathub build; it does not list {string.Join(" and ", missing)}.");
        }
        CommandResult result = await CommandRunner.RunAsync(context.DotNet, identity.ProjectDirectory,
            ["msbuild", identity.ProjectPath, "-nologo", $"-p:Configuration={context.Options.Configuration}", "-p:PublishAot=true",
                "-getProperty:NuGetLockFilePath,RestorePackagesWithLockFile,RunicApplicationFrontendDirectory"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new DevDevelopmentException(PackageCodes.PublishFailed, "dotnet msbuild could not evaluate the project's lock file.", result.CombinedOutput);
        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
        string Get(string name) => json.RootElement.GetProperty("Properties").TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";
        string lockFile = Path.GetFullPath(Get("NuGetLockFilePath") is { Length: > 0 } path ? path : "packages.lock.json", identity.ProjectDirectory);
        if (!File.Exists(lockFile))
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                $"The NativeAOT NuGet lock file {Path.GetFileName(lockFile)} does not exist. Set RestorePackagesWithLockFile, restore with dotnet restore -p:PublishAot=true and commit the lock file.",
                $"Lock file: {lockFile}\n");
        }
        IReadOnlyList<(FlathubNuGetPackage Package, string ContentHash)> locked = FlathubSources.ReadNuGetLock(await File.ReadAllTextAsync(lockFile, cancellationToken).ConfigureAwait(false));
        string? compiler = locked.Where(entry => string.Equals(entry.Package.Id, "Microsoft.DotNet.ILCompiler", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Package.Version).FirstOrDefault();
        if (compiler is null)
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                $"The lock file {Path.GetFileName(lockFile)} has no NativeAOT compiler (Microsoft.DotNet.ILCompiler). Give the NativeAOT restore its own lock file, <NuGetLockFilePath Condition=\"'$(PublishAot)' == 'true'\">packages.aot.lock.json</NuGetLockFilePath>, restore with -p:PublishAot=true and commit it.");
        }
        string lockText = await File.ReadAllTextAsync(lockFile, cancellationToken).ConfigureAwait(false);
        foreach (string runtime in FlathubSources.Runtimes)
        {
            if (!lockText.Contains($"/{runtime}\"", StringComparison.Ordinal))
            {
                throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                    $"The lock file {Path.GetFileName(lockFile)} has no {runtime} section; restore it again with RuntimeIdentifiers listing {runtime}.");
            }
        }
        return new AotEvaluation(lockFile, compiler, Get("RunicApplicationFrontendDirectory"), locked);
    }

    /// <summary>
    /// Restores the NativeAOT graph for each architecture in locked mode, so the NuGet cache holds every package the
    /// lock file names and the runtime packs the SDK downloads besides it, then lists both.
    /// </summary>
    private static async Task<(IReadOnlyList<FlathubNuGetPackage> Packages, string PackageRoot)> CollectNuGetAsync(PackageContext context, AotEvaluation aot, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        var packages = new List<FlathubNuGetPackage>(aot.Locked.Select(entry => entry.Package));
        string packageRoot = "";
        string empty = Path.Combine(Path.GetTempPath(), "runic-no-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            foreach (string runtime in FlathubSources.Runtimes)
            {
                string[] properties = ["-p:PublishAot=true", "-p:SelfContained=true", $"-p:RuntimeIdentifier={runtime}", $"-p:RuntimeFrameworkVersion={aot.RuntimeVersion}"];
                try
                {
                    await PackageTools.RunAsync(context.DotNet, ["restore", identity.ProjectPath, "--nologo", "--locked-mode", .. properties],
                        identity.ProjectDirectory, cancellationToken, PackageCodes.FlathubSourcesUnavailable).ConfigureAwait(false);
                }
                catch (DevDevelopmentException exception) when (exception.Code == PackageCodes.FlathubSourcesUnavailable)
                {
                    throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                        $"The locked NativeAOT restore for {runtime} failed: the lock file does not match the project. Restore it again with -p:PublishAot=true and commit it.",
                        exception.LocalDetail);
                }
                // Every pack the SDK would download, also those this machine's SDK has in its own packs folder, which the
                // Flathub SDK extension may not have in this version.
                CommandResult result = await CommandRunner.RunAsync(context.DotNet, identity.ProjectDirectory,
                    ["msbuild", identity.ProjectPath, "-nologo", "-t:ProcessFrameworkReferences", .. properties, $"-p:NetCoreTargetingPackRoot={empty}",
                        "-getItem:PackageDownload", "-getProperty:NuGetPackageRoot"], cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new DevDevelopmentException(PackageCodes.PublishFailed, "dotnet msbuild could not list the runtime packs.", result.CombinedOutput);
                using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
                packageRoot = json.RootElement.GetProperty("Properties").GetProperty("NuGetPackageRoot").GetString() ?? "";
                if (json.RootElement.TryGetProperty("Items", out JsonElement items) && items.TryGetProperty("PackageDownload", out JsonElement downloads))
                {
                    foreach (JsonElement download in downloads.EnumerateArray())
                    {
                        string id = download.GetProperty("Identity").GetString()!;
                        foreach (string version in (download.GetProperty("Version").GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                            packages.Add(new FlathubNuGetPackage(id, version.Trim('[', ']', ' ')));
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
        // The packs the SDK resolves from its own folder are not in the NuGet cache; download them once.
        IReadOnlyList<FlathubNuGetPackage> merged = FlathubSources.Merge(packages);
        string[] absent = [.. merged.Where(package => !File.Exists(CachePath(packageRoot, package, ".nupkg.sha512"))).Select(package => $"{package.Id}@{package.Version}")];
        if (absent.Length > 0)
        {
            string list = Path.Combine(context.Output, "runic-packs.proj");
            await File.WriteAllTextAsync(list, DownloadProject(absent), cancellationToken).ConfigureAwait(false);
            try
            {
                await PackageTools.RunAsync(context.DotNet, ["restore", list, "--nologo"], context.Output, cancellationToken, PackageCodes.FlathubSourcesUnavailable).ConfigureAwait(false);
            }
            finally
            {
                PackageTools.Delete(list);
                PackageTools.Delete(Path.Combine(context.Output, "obj"));
            }
        }
        // The lock file's packages are checked against its content hashes before their files' hashes are used.
        foreach ((FlathubNuGetPackage package, string contentHash) in aot.Locked)
        {
            string metadata = CachePath(packageRoot, package, ".nupkg.metadata", fileName: ".nupkg.metadata");
            string? cached = null;
            if (File.Exists(metadata))
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(metadata, cancellationToken).ConfigureAwait(false));
                cached = document.RootElement.TryGetProperty("contentHash", out JsonElement hash) ? hash.GetString() : null;
            }
            if (!string.Equals(cached, contentHash, StringComparison.Ordinal))
            {
                throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                    $"The NuGet cache's {package.Id} {package.Version} does not have the lock file's content hash; clear it from the cache and run the command again.");
            }
        }
        return (merged, packageRoot);
    }

    // The packs as PackageDownload items of a throwaway project, which dotnet restore puts in the NuGet cache.
    private static string DownloadProject(IEnumerable<string> packages)
    {
        var text = new StringBuilder().Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework><DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences></PropertyGroup>\n  <ItemGroup>\n");
        foreach (string package in packages)
        {
            int at = package.LastIndexOf('@');
            text.Append(CultureInfo.InvariantCulture, $"    <PackageDownload Include=\"{package[..at]}\" Version=\"[{package[(at + 1)..]}]\" />\n");
        }
        return text.Append("  </ItemGroup>\n</Project>\n").ToString();
    }

    private static string CachePath(string root, FlathubNuGetPackage package, string extension, string? fileName = null)
    {
        string id = package.Id.ToLowerInvariant();
        string version = package.Version.ToLowerInvariant();
        return Path.Combine(root, id, version, fileName ?? $"{id}.{version}{extension}");
    }

    /// <summary>
    /// The SHA-512 of the signed .nupkg that nuget.org serves, which NuGet keeps beside it in its cache. The lock file's
    /// contentHash is the hash without the repository signature, so it does not match the downloaded file.
    /// </summary>
    private static string NuGetSha512(string packageRoot, FlathubNuGetPackage package)
    {
        string path = CachePath(packageRoot, package, ".nupkg.sha512");
        if (!File.Exists(path))
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                $"{package.Id} {package.Version} is not in the NuGet cache after the restore, so its hash is unknown.", $"Expected: {path}\n");
        }
        return FlathubSources.HexFromBase64(File.ReadAllText(path));
    }

    /// <summary>The frontend directory with a package.json, which then needs a package-lock.json; null without a frontend.</summary>
    private static string? FindFrontend(string projectDirectory, string configured)
    {
        string directory = Path.GetFullPath(configured.Length != 0 ? configured : "Frontend", projectDirectory);
        if (!File.Exists(Path.Combine(directory, "package.json"))) return null;
        if (!File.Exists(Path.Combine(directory, "package-lock.json")))
        {
            throw new DevUsageException(PackageCodes.FlathubSourcesUnavailable,
                "The frontend has no package-lock.json. The Flathub build installs it with npm ci offline; use npm, or generate the sources with flatpak-builder-tools (docs/packaging.md#flathub).");
        }
        return directory;
    }

    /// <summary>The nearest directory with a .git entry, the repository the git source checks out.</summary>
    internal static string? FindGitRoot(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git"))) return current;
        }
        return null;
    }

    /// <summary>The flatpak-builder manifest.</summary>
    internal static string Manifest(FlathubBuild build, string icon, string? scalableIcon)
    {
        PackageIdentity identity = build.Identity;
        string exe = identity.Executable;
        string module = exe.ToLowerInvariant();
        var commands = new List<string>();
        if (build.HasNpmSources)
        {
            commands.Add("for tarball in flatpak-node/npm-tarballs/*.tgz; do npm cache add \"$tarball\" --cache=\"$PWD/flatpak-node/npm-cache\"; done");
            commands.Add($"cache=\"$PWD/flatpak-node/npm-cache\" && cd {Shell(build.FrontendPath!)} && npm ci --offline --no-audit --no-fund --cache=\"$cache\"");
        }
        commands.Add($"DOTNET_CLI_HOME=\"$PWD/.dotnet\" NUGET_PACKAGES=\"$PWD/.nuget/packages\" dotnet publish {Shell(build.ProjectPath)} -c Release -r \"$RUNIC_RUNTIME\" " +
            $"-p:PublishAot=true -p:RuntimeFrameworkVersion={build.RuntimeVersion} -p:RestoreLockedMode=true -p:NuGetAudit=false -p:CppCompilerAndLinker=gcc " +
            "-p:RunicApplicationFrontendInstallEnabled=false -p:PublishDocumentationFiles=false -p:PublishReferencesDocumentationFiles=false " +
            "--source \"$PWD/nuget-sources\" -o \"$PWD/publish\"");
        commands.Add("rm -rf publish/*.dbg publish/*.pdb");
        commands.Add($"mkdir -p {Shell($"/app/lib/{exe}")} /app/bin");
        commands.Add($"cp -R publish/. {Shell($"/app/lib/{exe}/")}");
        commands.Add($"printf '#!/bin/sh\\nexec \"/app/lib/{exe}/{exe}\" \"$@\"\\n' > {Shell($"/app/bin/{exe}")}");
        commands.Add($"chmod +x {Shell($"/app/bin/{exe}")}");
        commands.Add($"install -Dm644 {identity.Id}.desktop /app/share/applications/{identity.Id}.desktop");
        commands.Add($"install -Dm644 {identity.Id}.metainfo.xml /app/share/metainfo/{identity.Id}.metainfo.xml");
        if (identity.FileTypes.Count > 0) commands.Add($"install -Dm644 {identity.Id}.mime.xml /app/share/mime/packages/{identity.Id}.xml");
        if (identity.DBusActivatable) commands.Add($"install -Dm644 {identity.Id}.service /app/share/dbus-1/services/{identity.Id}.service");
        commands.Add($"install -Dm644 {Shell(icon)} /app/share/icons/hicolor/512x512/apps/{identity.Id}.png");
        if (scalableIcon is not null) commands.Add($"install -Dm644 {Shell(scalableIcon)} /app/share/icons/hicolor/scalable/apps/{identity.Id}.svg");

        var text = new StringBuilder();
        text.Append("# The Flathub manifest that dotnet runic package flathub generated: a source build of the application at the git\n");
        text.Append("# commit or tag below, offline from nuget-sources.json and npm-sources.json, which list the packages of the committed\n");
        text.Append("# lock files. Generate it again after a lock file changes. The .NET runtime is pinned to the NativeAOT compiler\n");
        text.Append("# of the lock file, so a newer .NET SDK extension restores the same packages.\n");
        text.Append("app-id: ").Append(identity.Id).Append('\n');
        text.Append("runtime: org.gnome.Platform\nruntime-version: '50'\nsdk: org.gnome.Sdk\nsdk-extensions:\n");
        text.Append("  - ").Append(DotNetExtension).Append('\n');
        if (build.HasNpmSources) text.Append("  - ").Append(NodeExtension).Append('\n');
        text.Append("command: ").Append(exe).Append('\n');
        text.Append("finish-args:\n  - --share=ipc\n  - --socket=wayland\n  - --socket=fallback-x11\n  - --device=dri\n");
        text.Append("  # Runic serves the page to the WebView over loopback.\n  - --share=network\n");
        text.Append("  # The Linux global menu: KDE Plasma's menu registrar (Runic.Desktop, global-menu-permission).\n  - --talk-name=com.canonical.AppMenu.Registrar\n");
        text.Append("build-options:\n  append-path: /usr/lib/sdk/dotnet10/bin").Append(build.HasNpmSources ? ":/usr/lib/sdk/node24/bin" : "").Append('\n');
        text.Append("  append-ld-library-path: /usr/lib/sdk/dotnet10/lib\n");
        text.Append("  env:\n    DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n    DOTNET_NOLOGO: '1'\n    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'\n");
        text.Append("  arch:\n    x86_64:\n      env:\n        RUNIC_RUNTIME: linux-x64\n    aarch64:\n      env:\n        RUNIC_RUNTIME: linux-arm64\n");
        text.Append("modules:\n  - name: ").Append(module).Append("\n    buildsystem: simple\n    build-commands:\n");
        foreach (string command in commands) text.Append("      - ").Append(Yaml(command)).Append('\n');
        text.Append("    sources:\n      - type: git\n        url: ").Append(Yaml(build.GitUrl)).Append('\n');
        if (build.GitCommit is not null) text.Append("        commit: ").Append(Yaml(build.GitCommit)).Append('\n');
        if (build.GitTag is not null) text.Append("        tag: ").Append(Yaml(build.GitTag)).Append('\n');
        text.Append("      - nuget-sources.json\n");
        if (build.HasNpmSources) text.Append("      - npm-sources.json\n");
        foreach (string file in DesktopFiles
            .Concat(identity.FileTypes.Count > 0 ? [".mime.xml"] : [])
            .Concat(identity.DBusActivatable ? [".service"] : []))
        {
            text.Append("      - type: file\n        path: ").Append(identity.Id).Append(file).Append('\n');
        }
        return text.ToString();
    }

    private static readonly string[] DesktopFiles = [".desktop", ".metainfo.xml"];

    // A single-quoted YAML scalar.
    private static string Yaml(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // A single-quoted shell word.
    private static string Shell(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '/' or '_' or '-')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
