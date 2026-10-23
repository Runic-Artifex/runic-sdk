using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package rpm</c>: an rpm of the staged tree from a generated spec, built with <c>rpmbuild -bb</c> (also on Debian
/// and Ubuntu). Automatic dependencies are off, because the NativeAOT binary loads GTK and WebKitGTK with dlopen;
/// WebKitGTK is required explicitly. Unsigned; sign it with rpmsign.
/// </summary>
internal static class RpmPackager
{
    /// <summary>The runtime dependency: WebKitGTK of the project's backend, which requires its GTK.</summary>
    internal static string Requires(bool gtk4) => gtk4 ? "webkitgtk6.0" : "webkit2gtk4.1";

    /// <summary>The spec, which copies the staged tree into the build root.</summary>
    internal static string Spec(PackageIdentity identity, string architecture, string stagedRoot, IReadOnlyList<string> paths)
    {
        PackageIdentity.CheckPackageName(identity.PackageName);
        static string Line(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace("%", "%%", StringComparison.Ordinal).Trim();
        var text = new StringBuilder();
        text.Append("Name: ").Append(identity.PackageName).Append('\n');
        text.Append("Version: ").Append(PackageIdentity.LinuxPackageVersion(identity.Version)).Append('\n');
        text.Append("Release: 1\n");
        text.Append("Summary: ").Append(Line(LinuxPackaging.Summary(identity))).Append('\n');
        text.Append("License: ").Append(Line(identity.License)).Append('\n');
        text.Append("URL: ").Append(Line(identity.Homepage)).Append('\n');
        text.Append("BuildArch: ").Append(architecture).Append('\n');
        text.Append("AutoReqProv: no\n");
        text.Append("Requires: ").Append(Requires(identity.Gtk4)).Append('\n');
        // The binary is already stripped and the tree already built: no debug package and no post-install processing.
        text.Append("%global debug_package %{nil}\n%global __os_install_post %{nil}\n%define _build_id_links none\n\n");
        text.Append("%description\n").Append(identity.Description.Replace("%", "%%", StringComparison.Ordinal).Trim()).Append("\n\n");
        text.Append("%install\nmkdir -p %{buildroot}\ncp -a '").Append(stagedRoot.Replace("'", "'\\''", StringComparison.Ordinal)).Append("/.' %{buildroot}/\n\n");
        text.Append("%files\n");
        foreach (string path in paths) text.Append('"').Append(path.Replace("%", "%%", StringComparison.Ordinal)).Append("\"\n");
        return text.ToString();
    }

    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        PackageIdentity.CheckPackageName(identity.PackageName);
        (_, string architecture) = LinuxPackaging.Architecture(context.Runtime);
        string rpmbuild = PackageTools.Require("rpmbuild", "rpm (apt install rpm, dnf install rpm-build)");
        string topdir = Path.Combine(context.Output, "rpmbuild");
        PackageTools.Delete(topdir);
        string root = Path.Combine(topdir, "STAGED");
        string entry = await LinuxPackaging.StageAsync(context, PackageTarget.Rpm, root, cancellationToken).ConfigureAwait(false);
        LinuxPackaging.LinkExecutable(identity, root);
        await LinuxPackaging.ValidateDesktopEntryAsync(context, entry, required: false, cancellationToken).ConfigureAwait(false);
        LinuxPackaging.NormalizeModes(root);
        string spec = LinuxPackaging.Write(Path.Combine(topdir, "SPECS", identity.PackageName + ".spec"),
            Spec(identity, architecture, root, LinuxPackaging.InstalledPaths(identity, root)));
        await PackageTools.RunAsync(rpmbuild, ["-bb", "--define", $"_topdir {topdir}", "--target", architecture, spec],
            context.Output, cancellationToken).ConfigureAwait(false);
        string built = Directory.EnumerateFiles(Path.Combine(topdir, "RPMS"), "*.rpm", SearchOption.AllDirectories).Single();
        string rpm = Path.Combine(context.Output, Path.GetFileName(built));
        File.Move(built, rpm, overwrite: true);
        PackageTools.Delete(topdir);
        context.Warn(PackageCodes.Unsigned, "The package is unsigned: sign it with rpmsign and your GPG key before you publish it.");
        context.Add(rpm, "rpm", "none");
    }
}
