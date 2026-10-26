using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package macos</c>: an .app bundle (the executable in Contents/MacOS, the frontend and icon in Contents/Resources)
/// signed with the hardened runtime, in a disk image. Ad-hoc signed unless the consumer passes a Developer ID with
/// <c>--sign</c>; notarized and stapled with <c>--notary-profile</c>. For osx-arm64 only: Runic does not target Intel Macs.
/// <c>--sandbox</c> (<c>RunicMacSandbox</c>) signs it for the App Sandbox, <c>--provisioning-profile</c> embeds a profile
/// and its identifiers, and <c>--pkg</c> also writes an installer package with productbuild, as App Store Connect
/// takes it (D045.5, D045.6). Nothing is uploaded.
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
        string? xcrun = context.Options.NotaryProfile is { Length: > 0 } ? PackageTools.Require("xcrun", "the Xcode command line tools (xcode-select --install)") : null;
        string? productbuild = context.Options.InstallerPackage ? PackageTools.Require("productbuild", "the Xcode command line tools (xcode-select --install)") : null;
        string? security = context.Options.ProvisioningProfile is { Length: > 0 } ? PackageTools.Require("security", "macOS") : null;
        string output = context.Output;
        bool sandbox = context.Options.Sandbox || identity.MacSandbox;
        string? profile = null;
        ProfileIdentity? profileIdentity = null;
        if (context.Options.ProvisioningProfile is { Length: > 0 } selectedProfile)
        {
            profile = Path.GetFullPath(selectedProfile, Environment.CurrentDirectory);
            if (!File.Exists(profile))
                throw new DevDevelopmentException(PackageCodes.ValidationFailed, "The provisioning profile does not exist.", $"Profile: {profile}\n");
            // The profile is a CMS-signed property list; security decodes it without the keychain.
            string decoded = await PackageTools.RunAsync(security!, ["cms", "-D", "-i", profile], output, cancellationToken,
                PackageCodes.ValidationFailed, capture: true).ConfigureAwait(false);
            profileIdentity = ReadProfile(decoded, identity.Id);
        }

        string app = Path.Combine(output, $"{identity.Name}.app");
        PackageTools.Delete(app);
        string macOS = Path.Combine(app, "Contents", "MacOS");
        string resources = Path.Combine(app, "Contents", "Resources");
        Directory.CreateDirectory(macOS);
        Directory.CreateDirectory(resources);
        string published = Path.Combine(output, $"publish-{context.Runtime}");
        await context.PublishAsync(context.Runtime, published, cancellationToken).ConfigureAwait(false);
        PackageTools.CopyFile(Path.Combine(published, identity.Executable), Path.Combine(macOS, identity.Executable));
        // Apple expects only code in Contents/MacOS; Runic finds the frontend in Contents/Resources/www. An application
        // that embeds its frontend (RunicApplicationFrontendDelivery=Embedded) publishes none.
        string www = Path.Combine(published, "www");
        if (Directory.Exists(www)) PackageTools.CopyDirectory(www, Path.Combine(resources, "www"));
        foreach (string entry in Directory.EnumerateFileSystemEntries(published))
        {
            string name = Path.GetFileName(entry);
            if (name == identity.Executable || name == "www") continue;
            if (Directory.Exists(entry)) PackageTools.CopyDirectory(entry, Path.Combine(macOS, name));
            else PackageTools.CopyFile(entry, Path.Combine(macOS, name));
        }
        await IcnsAsync(sips, iconutil, icon, Path.Combine(resources, "icon.icns"), output, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.MacOS);
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "Info.plist"),
            PackageManifests.Render(identity, "macos/Info.plist", fields), cancellationToken).ConfigureAwait(false);
        fields["Entitlements"] = Entitlements(sandbox, profileIdentity);
        string entitlementsText = PackageManifests.Render(identity, "macos/entitlements.plist", fields);
        CheckEntitlements(entitlementsText, sandbox, profileIdentity);
        string entitlements = Path.Combine(output, "entitlements.plist");
        await File.WriteAllTextAsync(entitlements, entitlementsText, cancellationToken).ConfigureAwait(false);
        if (profile is not null) PackageTools.CopyFile(profile, ProfilePath(app));

        string signingIdentity = context.Options.SigningIdentity is { Length: > 0 } selected ? selected : "-";
        bool adhoc = signingIdentity == "-";
        await PackageTools.RunAsync(codesign, CodesignArguments(signingIdentity, entitlements, app), output, cancellationToken).ConfigureAwait(false);
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
        if (context.Options.NotaryProfile is { Length: > 0 } notaryProfile)
        {
            await PackageTools.RunAsync(xcrun!, ["notarytool", "submit", dmg, "--keychain-profile", notaryProfile, "--wait"], output, cancellationToken).ConfigureAwait(false);
            await PackageTools.RunAsync(xcrun!, ["stapler", "staple", dmg], output, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Warn(PackageCodes.NotNotarized, adhoc
                ? "The bundle is ad-hoc signed and the disk image is not notarized: Gatekeeper blocks it on other Macs until you sign it with a Developer ID (--sign) and notarize it (--notary-profile)."
                : "The disk image is not notarized: Gatekeeper blocks it on other Macs until it is (--notary-profile).");
        }
        context.Add(dmg, "dmg", adhoc ? "none" : "identity");

        if (productbuild is not null)
        {
            string pkg = Path.Combine(output, $"{identity.Name}-{identity.Version}.pkg");
            PackageTools.Delete(pkg);
            string? installer = context.Options.InstallerSigningIdentity is { Length: > 0 } selectedInstaller ? selectedInstaller : null;
            await PackageTools.RunAsync(productbuild, ProductbuildArguments(app, installer, pkg), output, cancellationToken).ConfigureAwait(false);
            if (installer is null)
            {
                context.Warn(PackageCodes.Unsigned,
                    "The installer package is unsigned: App Store Connect and Gatekeeper accept it only signed with an installer identity (--installer-sign).");
            }
            else if (context.Options.NotaryProfile is { Length: > 0 } pkgProfile)
            {
                // A Developer ID Installer package outside the App Store is notarized as the disk image is.
                await PackageTools.RunAsync(xcrun!, ["notarytool", "submit", pkg, "--keychain-profile", pkgProfile, "--wait"], output, cancellationToken).ConfigureAwait(false);
                await PackageTools.RunAsync(xcrun!, ["stapler", "staple", pkg], output, cancellationToken).ConfigureAwait(false);
            }
            context.Add(pkg, "pkg", installer is null ? "none" : "identity");
        }
    }

    /// <summary>The App Sandbox entitlements that a Runic Desktop application needs.</summary>
    /// <remarks>
    /// Every page is served from a loopback listener with a WebSocket bridge, also to WKWebView, so the process listens
    /// (network.server) and WebKit connects to it (network.client). File dialogs grant the selected files
    /// (files.user-selected.read-write), and security-scoped bookmarks keep them (files.bookmarks.app-scope).
    /// </remarks>
    internal static readonly string[] SandboxEntitlements =
    [
        "com.apple.security.app-sandbox",
        "com.apple.security.network.server",
        "com.apple.security.network.client",
        "com.apple.security.files.user-selected.read-write",
        "com.apple.security.files.bookmarks.app-scope",
    ];

    /// <summary>What a provisioning profile grants the bundle: its application identifier and team.</summary>
    internal sealed record ProfileIdentity(string ApplicationIdentifier, string TeamIdentifier);

    /// <summary>The Entitlements field: the sandbox entitlements, and the profile's identifiers, which App Store Connect
    /// checks against the embedded profile.</summary>
    internal static string Entitlements(bool sandbox, ProfileIdentity? profile)
    {
        var text = new StringBuilder();
        if (sandbox)
        {
            foreach (string key in SandboxEntitlements) text.Append(CultureInfo.InvariantCulture, $"\n  <key>{key}</key><true/>");
        }
        if (profile is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  <key>com.apple.application-identifier</key><string>{PackageManifests.EscapeXml(profile.ApplicationIdentifier)}</string>");
            text.Append(CultureInfo.InvariantCulture, $"\n  <key>com.apple.developer.team-identifier</key><string>{PackageManifests.EscapeXml(profile.TeamIdentifier)}</string>");
        }
        return text.ToString();
    }

    /// <summary>
    /// Checks the filled entitlements: a property list without a key twice, which enables the sandbox when it was asked
    /// for and carries the profile's identifiers. An override without the Entitlements field fails here instead of
    /// signing a bundle that silently lacks them.
    /// </summary>
    internal static void CheckEntitlements(string text, bool sandbox, ProfileIdentity? profile)
    {
        Dictionary<string, XElement> entries;
        try
        {
            XElement dict = XDocument.Parse(text, LoadOptions.None).Root?.Element("dict")
                ?? throw new XmlException("The property list has no dict.");
            entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (XElement key in dict.Elements("key"))
            {
                if (!entries.TryAdd(key.Value, key.ElementsAfterSelf().FirstOrDefault() ?? key))
                    throw new DevDevelopmentException(PackageCodes.ValidationFailed, $"The manifest macos/entitlements.plist has the key {key.Value} twice.");
            }
        }
        catch (XmlException error)
        {
            throw new DevDevelopmentException(PackageCodes.ValidationFailed, "The manifest macos/entitlements.plist is not a property list.", error.Message + "\n");
        }
        var required = new List<string>();
        if (sandbox) required.AddRange(SandboxEntitlements);
        if (profile is not null) required.AddRange(["com.apple.application-identifier", "com.apple.developer.team-identifier"]);
        foreach (string key in required)
        {
            if (!entries.ContainsKey(key))
            {
                throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                    $"The manifest macos/entitlements.plist has no {key}. Add {{{{Entitlements}}}} to its dict, which fills it for --sandbox and --provisioning-profile.");
            }
        }
        if (sandbox && entries["com.apple.security.app-sandbox"].Name != "true")
            throw new DevDevelopmentException(PackageCodes.ValidationFailed, "The manifest macos/entitlements.plist turns com.apple.security.app-sandbox off.");
    }

    /// <summary>
    /// Reads a decoded provisioning profile (<c>security cms -D</c>): its team and the application identifier it grants,
    /// which must be <c>TEAMID.&lt;ApplicationId&gt;</c> or a wildcard that covers it.
    /// </summary>
    internal static ProfileIdentity ReadProfile(string plist, string bundleIdentifier)
    {
        XElement? root;
        try { root = XDocument.Parse(plist).Root?.Element("dict"); }
        catch (XmlException error)
        {
            throw new DevDevelopmentException(PackageCodes.ValidationFailed, "The provisioning profile is not a property list.", error.Message + "\n");
        }
        string? team = Value(root, "TeamIdentifier")?.Elements("string").FirstOrDefault()?.Value;
        string? granted = Value(Value(root, "Entitlements"), "com.apple.application-identifier")?.Value;
        if (team is null || granted is null)
        {
            throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                "The provisioning profile names no team or com.apple.application-identifier; use a macOS profile from your Apple Developer account.");
        }
        string expected = $"{team}.{bundleIdentifier}";
        bool covers = granted == expected
            || granted.EndsWith('*') && expected.StartsWith(granted[..^1], StringComparison.Ordinal) && granted.StartsWith(team + ".", StringComparison.Ordinal);
        if (!covers)
        {
            throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                $"The provisioning profile is for {granted}, not ApplicationId {bundleIdentifier} of team {team}.");
        }
        return new ProfileIdentity(expected, team);

        static XElement? Value(XElement? dict, string key) =>
            dict?.Elements("key").FirstOrDefault(element => element.Value == key)?.ElementsAfterSelf().FirstOrDefault();
    }

    /// <summary>Where the bundle carries its provisioning profile.</summary>
    internal static string ProfilePath(string app) => Path.Combine(app, "Contents", "embedded.provisionprofile");

    /// <summary>
    /// codesign for the bundle: the hardened runtime and the entitlements; a secure timestamp with a real identity, none
    /// ad hoc (<c>-</c>), which has no timestamp server.
    /// </summary>
    internal static IReadOnlyList<string> CodesignArguments(string signingIdentity, string entitlements, string app) =>
        ["--force", "--options", "runtime", .. signingIdentity == "-" ? Array.Empty<string>() : ["--timestamp"],
            "--entitlements", entitlements, "--sign", signingIdentity, app];

    /// <summary>
    /// productbuild for the bundle as a product archive that installs it in /Applications, as App Store Connect and
    /// Installer take it; signed with an installer identity when one is given.
    /// </summary>
    internal static IReadOnlyList<string> ProductbuildArguments(string app, string? installerIdentity, string pkg) =>
        ["--component", app, "/Applications", .. installerIdentity is null ? Array.Empty<string>() : ["--sign", installerIdentity], pkg];

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
            // Every size at 2x too: the Mac App Store requires the 512 px icon at 2x, 1024 px, which a 512 px source scales up.
            string twice = (size * 2).ToString(CultureInfo.InvariantCulture);
            await PackageTools.RunAsync(sips, ["-z", twice, twice, png, "--out", Path.Combine(iconset, $"icon_{size}x{size}@2x.png")],
                output, cancellationToken, capture: true).ConfigureAwait(false);
        }
        await PackageTools.RunAsync(iconutil, ["-c", "icns", iconset, "-o", destination], output, cancellationToken).ConfigureAwait(false);
        PackageTools.Delete(iconset);
    }
}
