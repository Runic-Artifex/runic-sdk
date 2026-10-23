using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package deb</c>: a Debian package of the staged tree, built with <c>dpkg-deb --root-owner-group</c>. GTK and
/// WebKitGTK are dependencies, for GTK 3 or, with a Runic.Desktop.Gtk4 reference, GTK 4. Unsigned; sign the repository.
/// </summary>
internal static class DebPackager
{
    /// <summary>The runtime dependencies: the C library, GTK and WebKitGTK of the project's backend.</summary>
    internal static string Depends(bool gtk4) => gtk4
        ? "libc6, libgtk-4-1, libwebkitgtk-6.0-4"
        : "libc6, libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0";

    /// <summary>The <c>DEBIAN/control</c> file.</summary>
    internal static string Control(PackageIdentity identity, string architecture, long installedKilobytes)
    {
        PackageIdentity.CheckPackageName(identity.PackageName);
        var text = new StringBuilder();
        text.Append("Package: ").Append(identity.PackageName).Append('\n');
        text.Append("Version: ").Append(PackageIdentity.LinuxPackageVersion(identity.Version)).Append('\n');
        text.Append("Architecture: ").Append(architecture).Append('\n');
        text.Append("Maintainer: ").Append(Maintainer(identity)).Append('\n');
        text.Append("Installed-Size: ").Append(installedKilobytes).Append('\n');
        text.Append("Depends: ").Append(Depends(identity.Gtk4)).Append('\n');
        text.Append("Section: utils\nPriority: optional\n");
        text.Append("Homepage: ").Append(identity.Homepage).Append('\n');
        text.Append("Description: ").Append(SingleLine(LinuxPackaging.Summary(identity))).Append('\n');
        // The extended description: each line indented by one space, an empty line as " .".
        foreach (string line in identity.Description.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            text.Append(' ').Append(line.Trim().Length == 0 ? "." : line.Trim()).Append('\n');
        return text.ToString();
    }

    /// <summary>The maintainer: the common name of <c>RunicApplicationPublisher</c>, otherwise the application's name.</summary>
    internal static string Maintainer(PackageIdentity identity)
    {
        string? commonName = identity.Publisher.Split(',').Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..].Trim();
        return SingleLine(string.IsNullOrEmpty(commonName) || commonName == "Example" ? identity.Name : commonName);
    }

    private static string SingleLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Trim();

    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        PackageIdentity.CheckPackageName(identity.PackageName);
        (string architecture, _) = LinuxPackaging.Architecture(context.Runtime);
        string dpkg = PackageTools.Require("dpkg-deb", "dpkg (apt install dpkg)");
        string version = PackageIdentity.LinuxPackageVersion(identity.Version);
        string root = Path.Combine(context.Output, $"{identity.PackageName}_{version}_{architecture}");
        string entry = await LinuxPackaging.StageAsync(context, PackageTarget.Deb, root, cancellationToken).ConfigureAwait(false);
        LinuxPackaging.LinkExecutable(identity, root);
        await LinuxPackaging.ValidateDesktopEntryAsync(context, entry, required: false, cancellationToken).ConfigureAwait(false);
        long size = LinuxPackaging.InstalledKilobytes(root);
        LinuxPackaging.Write(Path.Combine(root, "DEBIAN", "control"), Control(identity, architecture, size));
        LinuxPackaging.NormalizeModes(root);
        string deb = root + ".deb";
        PackageTools.Delete(deb);
        await PackageTools.RunAsync(dpkg, ["--build", "--root-owner-group", root, deb], context.Output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(root);
        context.Warn("The package is unsigned: sign the repository that serves it (for example with reprepro or aptly).");
        context.Add(deb, "deb", "none");
    }
}
