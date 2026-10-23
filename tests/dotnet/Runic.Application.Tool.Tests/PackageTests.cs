using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Runic.Application.Tool.Packaging;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// dotnet runic package: argument parsing, identity validation, manifest filling and overrides, and the payload. The
// packaging CI job builds, installs and opens the packages themselves on each operating system.
internal sealed partial class ToolTests
{
    private static PackageIdentity PackageIdentityFor(
        IReadOnlyList<string> schemes, IReadOnlyList<PackageFileType>? types = null, IReadOnlyList<string>? hosts = null, bool dbus = false,
        string? packagingDirectory = null) => new()
    {
        ProjectPath = Path.Combine(Path.GetTempPath(), "Notes", "Notes.csproj"),
        Id = "org.example.Notes",
        Name = "Notes & Co",
        Version = "2.1.0-preview.3",
        UrlSchemes = schemes,
        FileTypes = types ?? [],
        WebLinkHosts = hosts ?? [],
        DBusActivatable = dbus,
        Publisher = "CN=Example Ltd, O=Example Ltd",
        Description = "Notes <for> everyone",
        Homepage = "https://example.org/notes?a=1&b=2",
        Executable = "Notes",
        PackageName = "notes",
        Icon = Path.Combine(Path.GetTempPath(), "Notes", "icons", "icon.png"),
        PackagingDirectory = packagingDirectory ?? Path.Combine(Path.GetTempPath(), "runic-no-packaging-" + Guid.NewGuid().ToString("N")),
    };

    [Test, DisplayName("package accepts only IDs and schemes every platform registers")]
    public void PackageIdentityValidation()
    {
        foreach (string id in new[] { "org.example.Notes", "com.example.App2048Game", "a.b" })
            Equal(id, PackageIdentity.CheckApplicationId(id));
        foreach (string id in new[] { "", "Notes", "org.example.my-notes", "org.example.2048", "org..example", "org.example.Notes_App", $"org.example.{new string('a', 40)}" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.CheckApplicationId(id)).Code);
        SequenceEqual(["org.example.notes", "notes+x"], PackageIdentity.ParseUrlSchemes(" Org.Example.Notes; notes+x ,"));
        SequenceEqual([], PackageIdentity.ParseUrlSchemes(""));
        foreach (string scheme in new[] { "https", "file", "1notes", "no_tes" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.ParseUrlSchemes(scheme)).Code);
    }

    [Test, DisplayName("package accepts file types and web link hosts every platform declares")]
    public void PackageFileTypesAndHosts()
    {
        SequenceEqual(
            [new PackageFileType(".notes", "application/x-example-notes"), new PackageFileType(".tar.gz", null), new PackageFileType(".c+x", null)],
            PackageIdentity.ParseFileTypes(" .Notes=Application/X-Example-Notes; .tar.gz ,.c+x"));
        SequenceEqual([], PackageIdentity.ParseFileTypes(""));
        foreach (string entry in new[] { "notes", ".", "..notes", ".notes.", ".no*tes", ".notes=text", ".notes=a/b=c", ".notes;.NOTES" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.ParseFileTypes(entry)).Code);
        Equal("org.example.Notes.c-x", PackageIdentity.TypeIdentifier("org.example.Notes", ".c+x"));
        Equal("application/x-notes-tar.gz", PackageIdentity.LinuxMimeType("org.example.Notes", new PackageFileType(".tar.gz", null)));
        SequenceEqual(["notes.example.org", "links.example.com"], PackageIdentity.ParseWebLinkHosts("Notes.Example.org; links.example.com"));
        foreach (string host in new[] { "example", "*.example.org", "example.org:443", "https://example.org", "-a.example.org", "a_b.example.org" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.ParseWebLinkHosts(host)).Code);
    }

    [Test, DisplayName("package makes numeric and Linux package versions")]
    public void PackageVersions()
    {
        Equal("2.1.0.0", PackageIdentity.NumericVersion("2.1.0-preview.3", 4));
        Equal("1.2.0", PackageIdentity.NumericVersion("1.2", 3));
        Equal("1.2.3", PackageIdentity.NumericVersion("1.2.3.4+build", 3));
        Equal("1.0.0.0", PackageIdentity.NumericVersion(null, 4));
        Equal("1.2.3~preview.4", PackageIdentity.LinuxPackageVersion("1.2.3-preview.4"));
        Equal("1.2.3~rc.1.2", PackageIdentity.LinuxPackageVersion("1.2.3-rc-1.2+sha.abc"));
        Equal("1.2.3", PackageIdentity.LinuxPackageVersion("1.2.3"));
    }

    [Test, DisplayName("package reads the identity, packaging properties and GTK backend from the evaluated project")]
    public void PackageIdentityFromProperties()
    {
        string project = Path.Combine(Path.GetTempPath(), "Notes", "My_Notes.csproj");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicationId"] = "org.example.Notes",
            ["AssemblyName"] = "My_Notes",
            ["RunicApplicationUrlSchemes"] = "notes",
            ["RunicApplicationDBusActivatable"] = "True",
            ["RuntimeIdentifiers"] = "linux-x64; linux-arm64",
        };
        PackageIdentity identity = PackageIdentity.Create(project, properties, ["Runic.Application.Views.Desktop", "../Runic.Desktop.Gtk4/Runic.Desktop.Gtk4.csproj"], null);
        Equal("My_Notes", identity.Name);
        Equal("My_Notes", identity.Description);
        Equal("1.0.0", identity.Version);
        Equal("CN=Example", identity.Publisher);
        Equal("my-notes", identity.PackageName);
        Equal(Path.Combine(Path.GetTempPath(), "Notes", "icons", "icon.png"), identity.Icon);
        Equal(Path.Combine(Path.GetTempPath(), "Notes", "packaging"), identity.PackagingDirectory);
        True(identity.DBusActivatable, "RunicApplicationDBusActivatable is case-insensitive.");
        True(identity.Gtk4, "A Runic.Desktop.Gtk4 reference selects GTK 4.");
        SequenceEqual(["linux-x64", "linux-arm64"], identity.RuntimeIdentifiers);

        properties["RunicPackageIcon"] = "art/app.png";
        properties["RunicPackagingDirectory"] = "dist/manifests";
        properties["RunicPackageName"] = "example-notes";
        identity = PackageIdentity.Create(project, properties, [], "CN=Override");
        Equal(Path.Combine(Path.GetTempPath(), "Notes", "art", "app.png"), identity.Icon);
        Equal(Path.Combine(Path.GetTempPath(), "Notes", "dist", "manifests"), identity.PackagingDirectory);
        Equal("example-notes", identity.PackageName);
        Equal("CN=Override", identity.Publisher);
        False(identity.Gtk4, "Without Runic.Desktop.Gtk4 the backend is GTK 3.");
        Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.CheckPackageName("Example_Notes")).Code);

        properties["ApplicationId"] = "Notes";
        Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => PackageIdentity.Create(project, properties, [], null)).Code);
    }

    [Test, DisplayName("package fills every field of its manifests, escaped, with and without activation")]
    public void PackageManifestsFill()
    {
        Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() =>
            PackageManifests.Fill("{{Unknown}}", new Dictionary<string, string>(), "custom.xml")).Code);
        var targets = new Dictionary<PackageTarget, string[]>
        {
            [PackageTarget.Windows] = ["windows/AppxManifest.xml"],
            [PackageTarget.MacOS] = ["macos/Info.plist", "macos/entitlements.plist"],
            [PackageTarget.Flatpak] = ["linux/app.desktop", "linux/app.metainfo.xml", "linux/flatpak.yml", "linux/app.service"],
        };
        foreach ((PackageTarget target, string[] files) in targets)
        foreach (bool activation in new[] { false, true })
        {
            string[] schemes = activation ? ["org.example.notes", "notes-x"] : [];
            PackageFileType[] types = activation ? [new(".notes", "application/x-example-notes"), new(".tar.gz", null)] : [];
            PackageIdentity identity = PackageIdentityFor(schemes, types, activation ? ["notes.example.org"] : [], activation);
            Dictionary<string, string> fields = PackageManifests.Fields(identity, target, "win-arm64", new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
            foreach (string file in files)
            {
                string text = PackageManifests.Render(identity, file, fields);
                DoesNotContain(text, "{{");
                if (file.EndsWith(".xml", StringComparison.Ordinal) || file.EndsWith(".plist", StringComparison.Ordinal))
                {
                    XDocument.Parse(text);
                    if (file != "macos/entitlements.plist") Contains(text, "Notes &amp; Co");
                }
                switch (file)
                {
                    case "windows/AppxManifest.xml":
                        Contains(text, "Version=\"2.1.0.0\"");
                        Contains(text, "ProcessorArchitecture=\"arm64\"");
                        Contains(text, "Publisher=\"CN=Example Ltd, O=Example Ltd\"");
                        Equal(schemes.Length, Regex.Count(text, "<uap:Protocol Name=\""));
                        // The startup task is always declared, so Extensions is never empty.
                        Contains(text, "<desktop:StartupTask TaskId=\"LoginStartup\" Enabled=\"false\" DisplayName=\"Notes &amp; Co\" />");
                        Contains(text, "<desktop:Extension Category=\"windows.startupTask\" Executable=\"Notes.exe\" EntryPoint=\"Windows.FullTrustApplication\">");
                        Equal(types.Length, Regex.Count(text, "<uap3:FileTypeAssociation Name=\"(notes|tar\\.gz)\" MultiSelectModel=\"Player\">"));
                        Equal(activation, text.Contains("<uap:FileType ContentType=\"application/x-example-notes\">.notes</uap:FileType>", StringComparison.Ordinal));
                        Equal(activation, text.Contains("<uap3:Host Name=\"notes.example.org\" />", StringComparison.Ordinal));
                        Equal(activation, text.Contains("windows.appUriHandler", StringComparison.Ordinal));
                        break;
                    case "macos/Info.plist":
                        Contains(text, "<key>CFBundleShortVersionString</key><string>2.1.0</string>");
                        Equal(activation, text.Contains("CFBundleURLSchemes</key><array><string>org.example.notes</string><string>notes-x</string>", StringComparison.Ordinal));
                        Equal(types.Length, Regex.Count(text, "<key>LSItemContentTypes</key><array><string>org\\.example\\.Notes\\.(notes|tar\\.gz)</string>"));
                        Equal(types.Length, Regex.Count(text, "<key>UTTypeIdentifier</key><string>org\\.example\\.Notes\\.(notes|tar\\.gz)</string>"));
                        Equal(activation, text.Contains("<key>public.mime-type</key><array><string>application/x-example-notes</string>", StringComparison.Ordinal));
                        Equal(activation, text.Contains("<array><string>tar.gz</string></array>", StringComparison.Ordinal));
                        break;
                    case "linux/app.desktop":
                        True(Regex.IsMatch(text, "^Exec=Notes %U$", RegexOptions.Multiline), text);
                        Equal(activation ? "x-scheme-handler/org.example.notes;x-scheme-handler/notes-x;application/x-example-notes;application/x-notes-tar.gz;" : "",
                            Regex.Match(text, "^MimeType=(.*)$", RegexOptions.Multiline).Groups[1].Value);
                        Equal(activation, Regex.IsMatch(text, "^DBusActivatable=true$", RegexOptions.Multiline));
                        True(text.EndsWith('\n'), "The desktop entry ends with a newline.");
                        break;
                    case "linux/app.metainfo.xml":
                        Contains(text, "<developer id=\"org.example\">");
                        Contains(text, "<url type=\"homepage\">https://example.org/notes?a=1&amp;b=2</url>");
                        Contains(text, "<release version=\"2.1.0-preview.3\" date=\"2026-10-23\" />");
                        break;
                    case "linux/flatpak.yml":
                        True(Regex.IsMatch(text, "^app-id: org\\.example\\.Notes$", RegexOptions.Multiline), text);
                        Equal(activation, Regex.IsMatch(text, "install -Dm644 org\\.example\\.Notes\\.mime\\.xml /app/share/mime/packages/org\\.example\\.Notes\\.xml$", RegexOptions.Multiline));
                        Equal(activation, Regex.IsMatch(text, "install -Dm644 org\\.example\\.Notes\\.service /app/share/dbus-1/services/org\\.example\\.Notes\\.service$", RegexOptions.Multiline));
                        break;
                    case "linux/app.service":
                        Equal("[D-BUS Service]\nName=org.example.Notes\nExec=/app/bin/Notes\n", text);
                        break;
                }
            }
            if (target == PackageTarget.Flatpak && activation)
            {
                XDocument.Parse(fields["MimeInfo"]);
                True(Regex.IsMatch(fields["MimeInfo"], "<mime-type type=\"application/x-example-notes\">\\s*<comment>Notes &amp; Co document \\(\\.notes\\)</comment>\\s*<glob pattern=\"\\*\\.notes\"/>"), fields["MimeInfo"]);
            }
        }
    }

    [Test, DisplayName("package embeds the template's manifests and prefers the project's packaging directory")]
    public void PackageManifestOverrides()
    {
        string templates = Path.Combine(RepositoryRoot(), "tools", "Runic.Application.Templates", "content", "runic-app", "packaging");
        foreach (string name in PackageManifests.Names)
            Equal(File.ReadAllText(Path.Combine(templates, name.Replace('/', Path.DirectorySeparatorChar))), PackageManifests.ReadDefault(name));

        using var workspace = new TestWorkspace();
        PackageIdentity identity = PackageIdentityFor([], packagingDirectory: Path.Combine(workspace.Root, "packaging"));
        Equal(null, PackageManifests.FindOverride(identity, "windows/AppxManifest.xml"));
        string custom = workspace.Write("packaging/windows/AppxManifest.xml", "<Package Name=\"{{ApplicationId}}\" Version=\"{{PackageVersion}}\" />");
        Equal(custom, PackageManifests.FindOverride(identity, "windows/AppxManifest.xml"));
        Equal("<Package Name=\"org.example.Notes\" Version=\"2.1.0.0\" />",
            PackageManifests.Render(identity, "windows/AppxManifest.xml", PackageManifests.Fields(identity, PackageTarget.Windows, "win-x64")));
        // The other manifests still come from the tool.
        Equal(PackageManifests.ReadDefault("macos/Info.plist"), PackageManifests.Read(identity, "macos/Info.plist"));
        workspace.Write("packaging/macos/Info.plist", "{{UrlSchemes}}{{Publisher}}");
        Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() =>
            PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS))).Code);
    }

    [Test, DisplayName("package checks the host and the runtime identifiers of the target")]
    public void PackageRuntimes()
    {
        SequenceEqual(["win-x64"], PackageApplication.ResolveRuntimes(new PackageOptions(PackageTarget.Windows), "win", "x64"));
        SequenceEqual(["osx-arm64"], PackageApplication.ResolveRuntimes(new PackageOptions(PackageTarget.MacOS), "osx", "arm64"));
        SequenceEqual(["osx-arm64", "osx-x64"], PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.MacOS) { Runtimes = PackageOptions.ParseRuntimes("osx-arm64, osx-x64,osx-arm64") }, "osx", "arm64"));
        SequenceEqual(["win-arm64"], PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.Windows) { Runtimes = ["win-arm64"] }, "win", "x64"));
        foreach ((PackageOptions options, string host) in new[]
        {
            (new PackageOptions(PackageTarget.Windows), "linux"),
            (new PackageOptions(PackageTarget.MacOS), "win"),
            (new PackageOptions(PackageTarget.Windows) { Runtimes = ["linux-x64"] }, "win"),
            (new PackageOptions(PackageTarget.Windows) { Runtimes = ["win-x64", "win-arm64"] }, "win"),
        })
        {
            Equal(PackageCodes.WrongOperatingSystem, Throws<DevUsageException>(() => PackageApplication.ResolveRuntimes(options, host, "x64")).Code);
        }
        Equal("RAPPCLI1011", Throws<DevUsageException>(() => PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.Windows) { Runtimes = ["Windows X64"] }, "win", "x64")).Code);
    }

    [Test, DisplayName("package targets are in help and completion, and report RAPPPKG faults in JSON")]
    public void PackageCommandLine()
    {
        (int exitCode, string help) = RunCli("package", "--help");
        Equal(0, exitCode);
        foreach (string target in new[] { "windows", "macos", "flatpak", "appimage", "deb", "rpm" }) Contains(help, target);
        (_, string windows) = RunCli("package", "windows", "--help");
        foreach (string option in new[] { "--project", "--configuration", "--runtime", "--output-directory", "-o", "--publisher", "--certificate", "--thumbprint", "--timestamp-url" })
            Contains(windows, option);
        (_, string macos) = RunCli("package", "macos", "--help");
        foreach (string option in new[] { "--sign", "--notary-profile", "osx-arm64,osx-x64" })
            Contains(macos, option);
        (int completionExit, string completion) = RunCli("completion", "bash");
        Equal(0, completionExit);
        foreach (string word in new[] { "package", "--output-directory", "--notary-profile", "--thumbprint", "--gpg-sign", "appimage" })
            Contains(completion, word);

        // Conflicting and incomplete signing options are usage errors before any work.
        Equal(2, RunCli("package", "windows", "--certificate", "a.pfx", "--thumbprint", "00").ExitCode);
        Equal(2, RunCli("package", "macos", "--notary-profile", "notary").ExitCode);

        // A target of another operating system fails with RAPPPKG1004 and its catalog entry.
        string other = OperatingSystem.IsMacOS() ? "windows" : "macos";
        (int wrongExit, string wrong) = RunCli("package", other, "--format", "json");
        Equal(2, wrongExit);
        using JsonDocument document = JsonDocument.Parse(wrong);
        JsonElement fault = document.RootElement.GetProperty("fault");
        Equal(PackageCodes.WrongOperatingSystem, fault.GetProperty("code").GetString());
        True(fault.GetProperty("helpUri").GetString()!.EndsWith("/docs/diagnostics.md#rapppkg1004", StringComparison.Ordinal), wrong);
    }

    [Test, DisplayName("the package payload serializes as runic.application.tool.package/1")]
    public void PackagePayload()
    {
        var result = new PackageCommandResult("macos", new("org.example.Notes", "1.2.3"),
            [new("/out/Notes.app", "app", "adhoc"), new("/out/Notes-1.2.3.dmg", "dmg", "none")], ["not notarized"]);
        Equal(
            """{"target":"macos","identity":{"id":"org.example.Notes","version":"1.2.3"},"packages":[{"path":"/out/Notes.app","format":"app","signing":"adhoc"},{"path":"/out/Notes-1.2.3.dmg","format":"dmg","signing":"none"}],"warnings":["not notarized"]}""",
            JsonSerializer.Serialize(result, PackageCommandJsonContext.Default.PackageCommandResult));
        Equal("runic.application.tool.package/1", PackageCommandResult.PayloadType);
    }

    [Test, DisplayName("package reports a self-signed certificate as a test signature and hides the password")]
    public void PackageSigningKinds()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Runic Packaging Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        True(WindowsPackager.IsSelfSigned(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))), "self-signed");
        using RSA leafKey = RSA.Create(2048);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 issuer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var leaf = new CertificateRequest("CN=Example Ltd", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        False(WindowsPackager.IsSelfSigned(leaf.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3])), "issued");
        False(WindowsPackager.IsSelfSigned(null), "unknown");
        Equal("signtool sign /p *** \"C:\\a b.msix\"", PackageTools.Describe("signtool", ["sign", "/p", "secret", "C:\\a b.msix"], new HashSet<int> { 2 }));
    }

    [Test, DisplayName("package writes the deb control file and the rpm spec with the GTK backend's dependencies")]
    public void PackageLinuxMetadata()
    {
        PackageIdentity identity = PackageIdentityFor(["notes"]) with { Description = "Notes for everyone.\n\nWith 100% sync." };
        string control = DebPackager.Control(identity, "amd64", 2637);
        Equal("""
            Package: notes
            Version: 2.1.0~preview.3
            Architecture: amd64
            Maintainer: Example Ltd
            Installed-Size: 2637
            Depends: libc6, libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0
            Section: utils
            Priority: optional
            Homepage: https://example.org/notes?a=1&b=2
            Description: Notes for everyone
             Notes for everyone.
             .
             With 100% sync.

            """.Replace("\r", "", StringComparison.Ordinal), control);
        Contains(DebPackager.Control(identity with { Gtk4 = true, Publisher = "CN=Example" }, "arm64", 1),
            "Depends: libc6, libgtk-4-1, libwebkitgtk-6.0-4\n");
        Contains(DebPackager.Control(identity with { Publisher = "CN=Example" }, "arm64", 1), "Maintainer: Notes & Co\n");
        Equal(PackageCodes.IdentityInvalid, Throws<DevUsageException>(() => DebPackager.Control(identity with { PackageName = "Notes_App" }, "amd64", 1)).Code);

        string spec = RpmPackager.Spec(identity with { License = "MIT" }, "x86_64", "/tmp/stage dir/STAGED",
            ["/usr/lib/org.example.Notes", "/usr/bin/Notes", "/usr/share/applications/org.example.Notes.desktop"]);
        foreach (string line in new[]
        {
            "Name: notes\n", "Version: 2.1.0~preview.3\n", "Release: 1\n", "Summary: Notes for everyone\n", "License: MIT\n",
            "BuildArch: x86_64\n", "AutoReqProv: no\n", "Requires: webkit2gtk4.1\n", "%global debug_package %{nil}\n",
            "With 100%% sync.", "cp -a '/tmp/stage dir/STAGED/.' %{buildroot}/\n",
            "%files\n\"/usr/lib/org.example.Notes\"\n\"/usr/bin/Notes\"\n\"/usr/share/applications/org.example.Notes.desktop\"\n",
        })
        {
            Contains(spec, line);
        }
        Contains(RpmPackager.Spec(identity with { Gtk4 = true }, "aarch64", "/s", []), "Requires: webkitgtk6.0\n");

        Equal(("amd64", "x86_64"), LinuxPackaging.Architecture("linux-x64"));
        Equal(("arm64", "aarch64"), LinuxPackaging.Architecture("linux-arm64"));
        Equal(PackageCodes.WrongOperatingSystem, Throws<DevUsageException>(() => LinuxPackaging.Architecture("linux-musl-x86")).Code);
    }

    [Test, DisplayName("package lays out the AppImage and the Linux fields of each format")]
    public void PackageLinuxFormats()
    {
        PackageIdentity identity = PackageIdentityFor(["notes"], dbus: true);
        Equal("Notes_&_Co-2.1.0-preview.3-x86_64.AppImage".Replace("&", "_", StringComparison.Ordinal), AppImagePackager.FileName(identity, "x86_64"));
        string appRun = AppImagePackager.AppRun(identity);
        True(appRun.StartsWith("#!/bin/sh\n", StringComparison.Ordinal) && appRun.EndsWith('\n'), appRun);
        Contains(appRun, "exec \"$here/usr/lib/org.example.Notes/Notes\" \"$@\"");

        // The service file starts the launcher where each format installs it; an AppImage has no fixed path, so it is
        // not D-Bus activatable.
        foreach ((PackageTarget target, string bin, bool activatable) in new[]
        {
            (PackageTarget.Flatpak, "/app/bin", true), (PackageTarget.Deb, "/usr/bin", true), (PackageTarget.Rpm, "/usr/bin", true),
            (PackageTarget.AppImage, "/usr/bin", false),
        })
        {
            Dictionary<string, string> fields = PackageManifests.Fields(identity, target);
            Equal($"[D-BUS Service]\nName=org.example.Notes\nExec={bin}/Notes\n", PackageManifests.Render(identity, "linux/app.service", fields));
            Equal(activatable, PackageManifests.Render(identity, "linux/app.desktop", fields).Contains("DBusActivatable=true\n", StringComparison.Ordinal));
        }

        // The Flatpak installs the scalable icon only when an icon.svg is beside the PNG.
        using var workspace = new TestWorkspace();
        string png = workspace.Write("icons/icon.png", "png");
        PackageIdentity withIcon = identity with { Icon = png };
        DoesNotContain(PackageManifests.Fields(withIcon, PackageTarget.Flatpak)["ExtraInstalls"], "icon.svg");
        workspace.Write("icons/icon.svg", "<svg/>");
        Contains(PackageManifests.Fields(withIcon, PackageTarget.Flatpak)["ExtraInstalls"],
            "install -Dm644 icon.svg /app/share/icons/hicolor/scalable/apps/org.example.Notes.svg");
    }

    private static string RepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "eng", "workspace.json"))) return directory;
        }
        throw new InvalidOperationException("The repository root was not found.");
    }
}
