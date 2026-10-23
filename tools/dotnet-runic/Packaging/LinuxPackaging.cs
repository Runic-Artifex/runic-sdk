using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// What the Linux formats share: the architecture names, desktop entry validation and the staged installation tree of
/// deb and rpm (<c>/usr/lib/&lt;id&gt;/</c>, the <c>/usr/bin</c> link, the desktop entry, icons, metainfo, MIME
/// definitions and the D-Bus service file).
/// </summary>
internal static class LinuxPackaging
{
    /// <summary>The architecture of a Linux RID as Debian, rpm and AppImage name it.</summary>
    internal static (string Debian, string Rpm) Architecture(string runtime) =>
        runtime.EndsWith("-arm64", StringComparison.Ordinal) ? ("arm64", "aarch64")
        : runtime.EndsWith("-x64", StringComparison.Ordinal) ? ("amd64", "x86_64")
        : throw new DevUsageException(PackageCodes.WrongOperatingSystem,
            $"The Linux packages are built for linux-x64 and linux-arm64; --runtime {runtime} is not supported.");

    /// <summary>
    /// Validates the desktop entry with desktop-file-validate (<c>RAPPPKG1006</c> when it rejects it); without the tool,
    /// a warning, unless the target requires it.
    /// </summary>
    internal static async Task ValidateDesktopEntryAsync(PackageContext context, string entry, bool required, CancellationToken cancellationToken)
    {
        string? validator = required
            ? PackageTools.Require("desktop-file-validate", "desktop-file-utils")
            : PackageTools.Find("desktop-file-validate");
        if (validator is null)
        {
            context.Warn("desktop-file-validate (desktop-file-utils) is not installed, so the desktop entry was not validated.");
            return;
        }
        await PackageTools.RunAsync(validator, [entry], context.Output, cancellationToken, PackageCodes.ValidationFailed).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes into <c>&lt;root&gt;/usr/lib/&lt;id&gt;</c> and installs the desktop integration below
    /// <c>&lt;root&gt;/usr/share</c>: the files a deb, an rpm and an AppDir have in common. Returns the desktop entry.
    /// </summary>
    internal static async Task<string> StageAsync(PackageContext context, PackageTarget target, string root, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        string icon = context.RequireIcon();
        PackageTools.Delete(root);
        string library = Path.Combine(root, "usr", "lib", identity.Id);
        await context.PublishAsync(context.Runtime, library, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, target);
        string share = Path.Combine(root, "usr", "share");
        string entry = Write(Path.Combine(share, "applications", identity.Id + ".desktop"), PackageManifests.Render(identity, "linux/app.desktop", fields));
        Write(Path.Combine(share, "metainfo", identity.Id + ".metainfo.xml"), PackageManifests.Render(identity, "linux/app.metainfo.xml", fields));
        if (fields["MimeInfo"].Length != 0)
            Write(Path.Combine(share, "mime", "packages", identity.Id + ".xml"), fields["MimeInfo"]);
        if (identity.DBusActivatable && target != PackageTarget.AppImage)
            Write(Path.Combine(share, "dbus-1", "services", identity.Id + ".service"), PackageManifests.Render(identity, "linux/app.service", fields));
        Copy(icon, Path.Combine(share, "icons", "hicolor", "512x512", "apps", identity.Id + ".png"));
        if (identity.ScalableIcon is { } svg)
            Copy(svg, Path.Combine(share, "icons", "hicolor", "scalable", "apps", identity.Id + ".svg"));
        return entry;
    }

    /// <summary>The <c>/usr/bin</c> link to the executable, relative, so the staged tree can move.</summary>
    internal static void LinkExecutable(PackageIdentity identity, string root)
    {
        string bin = Path.Combine(root, "usr", "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, identity.Executable), $"../lib/{identity.Id}/{identity.Executable}");
    }

    /// <summary>
    /// Directories 0755, executables 0755 and other files 0644, as packages install them; symbolic links unchanged.
    /// </summary>
    internal static void NormalizeModes(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode directory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(root, directory);
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.LinkTarget is not null) continue;
            if (entry is DirectoryInfo) File.SetUnixFileMode(entry.FullName, directory);
            else File.SetUnixFileMode(entry.FullName, (entry.UnixFileMode & UnixFileMode.UserExecute) != 0 ? directory : file);
        }
    }

    /// <summary>The installed paths below the staged root: the application directory as one entry, then every other file.</summary>
    internal static IReadOnlyList<string> InstalledPaths(PackageIdentity identity, string root)
    {
        string library = Path.Combine(root, "usr", "lib", identity.Id);
        var paths = new List<string> { $"/usr/lib/{identity.Id}" };
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (path == library || path.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null) continue;
            paths.Add("/" + Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
        }
        return paths;
    }

    /// <summary>The installed size in KiB, as Debian's Installed-Size counts it.</summary>
    internal static long InstalledKilobytes(string root) =>
        (new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Where(file => file.LinkTarget is null).Sum(file => file.Length) + 1023) / 1024;

    internal static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>The summary line of a package: the description's first line, without a final period.</summary>
    internal static string Summary(PackageIdentity identity)
    {
        string line = identity.Description.Split('\n', 2)[0].Trim();
        return line.EndsWith('.') ? line[..^1] : line;
    }
}
