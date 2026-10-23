using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package flatpak</c>: a Flatpak bundle on the GNOME runtime, which provides GTK 4 and WebKitGTK 6, from the
/// NativeAOT publish folder. The desktop entry and metainfo are validated first. Unsigned unless the consumer passes
/// <c>--gpg-sign</c> with a key of its own.
/// </summary>
internal static class FlatpakPackager
{
    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        string icon = context.RequireIcon();
        string builder = PackageTools.Require("flatpak-builder", "flatpak-builder");
        string flatpak = PackageTools.Require("flatpak", "flatpak");
        string appstreamcli = PackageTools.Require("appstreamcli", "appstream");
        string output = context.Output;
        string stage = Path.Combine(output, "flatpak-source");
        PackageTools.Delete(stage);
        await context.PublishAsync(context.Runtime, Path.Combine(stage, "publish"), cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.Flatpak);
        string entry = LinuxPackaging.Write(Path.Combine(stage, identity.Id + ".desktop"), PackageManifests.Render(identity, "linux/app.desktop", fields));
        string metainfo = LinuxPackaging.Write(Path.Combine(stage, identity.Id + ".metainfo.xml"), PackageManifests.Render(identity, "linux/app.metainfo.xml", fields));
        if (fields["MimeInfo"].Length != 0) LinuxPackaging.Write(Path.Combine(stage, identity.Id + ".mime.xml"), fields["MimeInfo"]);
        if (identity.DBusActivatable)
            LinuxPackaging.Write(Path.Combine(stage, identity.Id + ".service"), PackageManifests.Render(identity, "linux/app.service", fields));
        File.Copy(icon, Path.Combine(stage, "icon.png"), overwrite: true);
        if (identity.ScalableIcon is { } svg) File.Copy(svg, Path.Combine(stage, "icon.svg"), overwrite: true);
        string manifest = LinuxPackaging.Write(Path.Combine(output, identity.Id + ".yml"), PackageManifests.Render(identity, "linux/flatpak.yml", fields));
        await LinuxPackaging.ValidateDesktopEntryAsync(context, entry, required: true, cancellationToken).ConfigureAwait(false);
        await PackageTools.RunAsync(appstreamcli, ["validate", "--no-net", "--explain", metainfo], output, cancellationToken, PackageCodes.ValidationFailed)
            .ConfigureAwait(false);

        List<string> sign = context.Options.GpgKey is { Length: > 0 } key ? [$"--gpg-sign={key}"] : [];
        string repository = Path.Combine(output, "repo");
        string build = Path.Combine(output, "build");
        await PackageTools.RunAsync(builder, ["--user", "--install-deps-from=flathub", "--force-clean", "--disable-rofiles-fuse",
            $"--state-dir={Path.Combine(output, ".flatpak-builder")}", $"--repo={repository}", .. sign, build, manifest], output, cancellationToken)
            .ConfigureAwait(false);
        string bundle = Path.Combine(output, identity.Id + ".flatpak");
        await PackageTools.RunAsync(flatpak, ["build-bundle", .. sign, repository, bundle, identity.Id], output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(build);
        if (sign.Count == 0) context.Warn(PackageCodes.Unsigned, "The repository and bundle are unsigned (--gpg-sign signs them with your key).");
        context.Add(bundle, "flatpak", sign.Count == 0 ? "none" : "identity");
    }
}
