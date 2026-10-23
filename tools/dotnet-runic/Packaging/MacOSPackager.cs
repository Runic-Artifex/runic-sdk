using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package macos</c>: an .app bundle (the executable in Contents/MacOS, the frontend and icon in Contents/Resources)
/// signed with the hardened runtime, in a disk image. Ad-hoc signed unless the consumer passes a Developer ID with
/// <c>--sign</c>; notarized and stapled with <c>--notary-profile</c>. Several runtimes make a universal binary.
/// </summary>
internal static class MacOSPackager
{
    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        string icon = context.RequireIcon();
        string codesign = PackageTools.Require("codesign", "the Xcode command line tools (xcode-select --install)");
        string sips = PackageTools.Require("sips", "macOS");
        string iconutil = PackageTools.Require("iconutil", "the Xcode command line tools (xcode-select --install)");
        string ditto = PackageTools.Require("ditto", "macOS");
        string hdiutil = PackageTools.Require("hdiutil", "macOS");
        string? lipo = context.Runtimes.Count > 1 ? PackageTools.Require("lipo", "the Xcode command line tools (xcode-select --install)") : null;
        string? xcrun = context.Options.NotaryProfile is { Length: > 0 } ? PackageTools.Require("xcrun", "the Xcode command line tools (xcode-select --install)") : null;
        string output = context.Output;

        string app = Path.Combine(output, $"{identity.Name}.app");
        PackageTools.Delete(app);
        string macOS = Path.Combine(app, "Contents", "MacOS");
        string resources = Path.Combine(app, "Contents", "Resources");
        Directory.CreateDirectory(macOS);
        Directory.CreateDirectory(resources);
        var published = new List<string>();
        foreach (string runtime in context.Runtimes)
        {
            string directory = Path.Combine(output, $"publish-{runtime}");
            await context.PublishAsync(runtime, directory, cancellationToken).ConfigureAwait(false);
            published.Add(directory);
        }
        string executable = Path.Combine(macOS, identity.Executable);
        if (published.Count == 1)
        {
            PackageTools.CopyFile(Path.Combine(published[0], identity.Executable), executable);
        }
        else
        {
            await PackageTools.RunAsync(lipo!, ["-create", "-output", executable, .. published.Select(directory => Path.Combine(directory, identity.Executable))],
                output, cancellationToken).ConfigureAwait(false);
        }
        // Apple expects only code in Contents/MacOS; Runic finds the frontend in Contents/Resources/www. An application
        // that embeds its frontend (RunicApplicationFrontendDelivery=Embedded) publishes none.
        string www = Path.Combine(published[0], "www");
        if (Directory.Exists(www)) PackageTools.CopyDirectory(www, Path.Combine(resources, "www"));
        foreach (string entry in Directory.EnumerateFileSystemEntries(published[0]))
        {
            string name = Path.GetFileName(entry);
            if (name == identity.Executable || name == "www") continue;
            if (Directory.Exists(entry)) PackageTools.CopyDirectory(entry, Path.Combine(macOS, name));
            else PackageTools.CopyFile(entry, Path.Combine(macOS, name));
        }
        await IcnsAsync(sips, iconutil, icon, Path.Combine(resources, "icon.icns"), output, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "Info.plist"),
            PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS)), cancellationToken).ConfigureAwait(false);
        string entitlements = Path.Combine(output, "entitlements.plist");
        await File.WriteAllTextAsync(entitlements,
            PackageManifests.Render(identity, "macos/entitlements.plist", PackageManifests.Fields(identity, PackageTarget.MacOS)), cancellationToken).ConfigureAwait(false);

        string signingIdentity = context.Options.SigningIdentity is { Length: > 0 } selected ? selected : "-";
        bool adhoc = signingIdentity == "-";
        List<string> timestamp = adhoc ? [] : ["--timestamp"];
        await PackageTools.RunAsync(codesign, ["--force", "--options", "runtime", .. timestamp, "--entitlements", entitlements, "--sign", signingIdentity, app],
            output, cancellationToken).ConfigureAwait(false);
        await PackageTools.RunAsync(codesign, ["--verify", "--strict", "--verbose=2", app], output, cancellationToken, PackageCodes.ValidationFailed).ConfigureAwait(false);
        File.Delete(entitlements);
        context.Add(app, "app", adhoc ? "adhoc" : "identity");

        string staging = Path.Combine(output, "dmg");
        PackageTools.Delete(staging);
        Directory.CreateDirectory(staging);
        await PackageTools.RunAsync(ditto, [app, Path.Combine(staging, Path.GetFileName(app))], output, cancellationToken).ConfigureAwait(false);
        File.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications");
        string dmg = Path.Combine(output, $"{identity.Name}-{identity.Version}.dmg");
        PackageTools.Delete(dmg);
        await PackageTools.RunAsync(hdiutil, ["create", "-volname", identity.Name, "-srcfolder", staging, "-format", "UDZO", "-ov", dmg],
            output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(staging);
        if (!adhoc)
            await PackageTools.RunAsync(codesign, ["--force", "--timestamp", "--sign", signingIdentity, dmg], output, cancellationToken).ConfigureAwait(false);
        if (context.Options.NotaryProfile is { Length: > 0 } profile)
        {
            await PackageTools.RunAsync(xcrun!, ["notarytool", "submit", dmg, "--keychain-profile", profile, "--wait"], output, cancellationToken).ConfigureAwait(false);
            await PackageTools.RunAsync(xcrun!, ["stapler", "staple", dmg], output, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Warn(PackageCodes.NotNotarized, adhoc
                ? "The bundle is ad-hoc signed and the disk image is not notarized: Gatekeeper blocks it on other Macs until you sign it with a Developer ID (--sign) and notarize it (--notary-profile)."
                : "The disk image is not notarized: Gatekeeper blocks it on other Macs until it is (--notary-profile).");
        }
        context.Add(dmg, "dmg", adhoc ? "none" : "identity");
    }

    /// <summary>An .icns from the 512 px PNG: iconutil reads an .iconset folder of the standard sizes.</summary>
    private static async Task IcnsAsync(string sips, string iconutil, string png, string destination, string output, CancellationToken cancellationToken)
    {
        string iconset = Path.Combine(output, "icon.iconset");
        PackageTools.Delete(iconset);
        Directory.CreateDirectory(iconset);
        foreach (int size in new[] { 16, 32, 128, 256, 512 })
        {
            string text = size.ToString(CultureInfo.InvariantCulture);
            await PackageTools.RunAsync(sips, ["-z", text, text, png, "--out", Path.Combine(iconset, $"icon_{size}x{size}.png")],
                output, cancellationToken, capture: true).ConfigureAwait(false);
            if (size <= 256)
            {
                string twice = (size * 2).ToString(CultureInfo.InvariantCulture);
                await PackageTools.RunAsync(sips, ["-z", twice, twice, png, "--out", Path.Combine(iconset, $"icon_{size}x{size}@2x.png")],
                    output, cancellationToken, capture: true).ConfigureAwait(false);
            }
        }
        await PackageTools.RunAsync(iconutil, ["-c", "icns", iconset, "-o", destination], output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(iconset);
    }
}
