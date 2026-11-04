using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.CheckApplicationId(id)).Code);
        SequenceEqual(["org.example.notes", "notes+x"], PackageIdentity.ParseUrlSchemes(" Org.Example.Notes; notes+x ,"));
        SequenceEqual([], PackageIdentity.ParseUrlSchemes(""));
        foreach (string scheme in new[] { "https", "file", "1notes", "no_tes" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.ParseUrlSchemes(scheme)).Code);
    }

    [Test, DisplayName("package accepts file types and web link hosts every platform declares")]
    public void PackageFileTypesAndHosts()
    {
        SequenceEqual(
            [new PackageFileType(".notes", "application/x-example-notes"), new PackageFileType(".tar.gz", null), new PackageFileType(".c+x", null)],
            PackageIdentity.ParseFileTypes(" .Notes=Application/X-Example-Notes; .tar.gz ,.c+x"));
        SequenceEqual([], PackageIdentity.ParseFileTypes(""));
        foreach (string entry in new[] { "notes", ".", "..notes", ".notes.", ".no*tes", ".notes=text", ".notes=a/b=c", ".notes;.NOTES" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.ParseFileTypes(entry)).Code);
        Equal("org.example.Notes.c-x", PackageIdentity.TypeIdentifier("org.example.Notes", ".c+x"));
        Equal("application/x-notes-tar.gz", PackageIdentity.LinuxMimeType("org.example.Notes", new PackageFileType(".tar.gz", null)));
        SequenceEqual(["notes.example.org", "links.example.com"], PackageIdentity.ParseWebLinkHosts("Notes.Example.org; links.example.com"));
        foreach (string host in new[] { "example", "*.example.org", "example.org:443", "https://example.org", "-a.example.org", "a_b.example.org" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.ParseWebLinkHosts(host)).Code);
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
        Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.CheckPackageName("Example_Notes")).Code);

        properties["ApplicationId"] = "Notes";
        Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.Create(project, properties, [], null)).Code);
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
                        // Desktop entry strings, not XML: no &amp; or &lt;.
                        Contains(text, "\nName=Notes & Co\nComment=Notes <for> everyone\n");
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
                        // Every generated manifest lets the app reach the global menu registrar, with or without a menu bar.
                        Contains(text, "  - --talk-name=com.canonical.AppMenu.Registrar\n");
                        // Filled fields never land in the header comment, where a multi-line field breaks the YAML.
                        True(text.Split('\n').TakeWhile(line => !line.StartsWith("app-id:", StringComparison.Ordinal)).All(line => line.StartsWith('#')), text);
                        True(text.IndexOf("      - install", StringComparison.Ordinal) > text.IndexOf("build-commands:", StringComparison.Ordinal), text);
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

    [Test, DisplayName("package reads RunicApplicationLocales and the RunicApplicationLocale items")]
    public void PackageApplicationLocales()
    {
        string project = Path.Combine(Path.GetTempPath(), "Notes", "Notes.csproj");
        var properties = new Dictionary<string, string>
        {
            ["ApplicationId"] = "org.example.Notes",
            ["RunicApplicationLocales"] = "en; de;pt_br;zh-hant;sr-latn-rs;ES-419;ca_es_VALENCIA",
        };
        PackageIdentity identity = PackageIdentity.Create(project, properties, [], null,
            [new("de", "Notizen", "Notizen für alle"), new("fr", "Notes", null), new("EN", null, "Notes for everyone")]);
        Equal("en,de,pt-BR,zh-Hant,sr-Latn-RS,es-419,ca-ES-valencia,fr", string.Join(',', identity.Locales.Select(locale => locale.Tag)));
        Equal(new PackageLocale("de", "Notizen", "Notizen für alle"), identity.Locales[1]);
        Equal(new PackageLocale("en", null, "Notes for everyone"), identity.Locales[0]);
        Equal(new PackageLocale("fr", "Notes", null), identity.Locales[7]);
        Equal(0, PackageIdentity.Create(project, new Dictionary<string, string> { ["ApplicationId"] = "org.example.Notes" }, [], null).Locales.Count);
        foreach (string invalid in new[] { "english", "de-", "x", "de-DE-val", "de-DE-x-private", "de/AT" })
        {
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.Create(project,
                new Dictionary<string, string> { ["ApplicationId"] = "org.example.Notes", ["RunicApplicationLocales"] = invalid }, [], null)).Code);
        }
        Equal("de-DE-1996", PackageIdentity.CanonicalLocale("DE_de_1996"));
    }

    [Test, DisplayName("package maps language tags to glibc locales for desktop entries and AppStream")]
    [Arguments("de", "de", true)]
    [Arguments("pt-BR", "pt_BR", true)]
    [Arguments("es-419", "es", false)]
    [Arguments("sr-Latn", "sr@latin", true)]
    [Arguments("sr-Latn-RS", "sr_RS@latin", true)]
    [Arguments("uz-Cyrl", "uz@cyrillic", true)]
    [Arguments("ks-Deva", "ks@devanagari", true)]
    [Arguments("zh-Hans", "zh_CN", false)]
    [Arguments("zh-Hant", "zh_TW", false)]
    [Arguments("zh-TW", "zh_TW", true)]
    [Arguments("zh-Hant-HK", "zh_HK", true)]
    [Arguments("pa-Arab", "pa", false)]
    [Arguments("ca-ES-valencia", "ca@valencia", false)]
    [Arguments("ca-valencia", "ca@valencia", true)]
    [Arguments("sr-Latn-RS-ekavsk", "sr_RS@latin", false)]
    [Arguments("de-DE-1901-1996", "de@1901", false)]
    public void PackageLinuxLocale(string tag, string locale, bool exact) =>
        Equal((locale, exact), PackageIdentity.LinuxLocale(tag));

    [Test, DisplayName("package names each Linux locale once, preferring a tag the locale keeps whole")]
    public void PackageLinuxLocaleDeduplication()
    {
        IReadOnlyList<PackageLocale> linux = PackageIdentity.LinuxLocales(
        [
            new("en", null, null),
            new("zh-Hant", "Hant name", "Hant description"),
            new("zh-TW", "TW name", null),
            new("es-419", "419 name", "419 description"),
            new("es", null, "es description"),
            new("pa-Arab", "Arab name", null),
            new("pa", "pa name", null),
            new("zh-Hans", "Hans name", null),
            new("ca-ES-valencia", "Valencia", null),
        ]);
        // The order of first appearance; the exact tag wins each value it has, a lossy one fills the rest.
        Equal("zh_TW,es,pa,zh_CN,ca@valencia", string.Join(',', linux.Select(locale => locale.Tag)));
        Equal(new PackageLocale("zh_TW", "TW name", "Hant description"), linux[0]);
        Equal(new PackageLocale("es", "419 name", "es description"), linux[1]);
        Equal(new PackageLocale("pa", "pa name", null), linux[2]);
        Equal(new PackageLocale("zh_CN", "Hans name", null), linux[3]);

        // The desktop entry and the metainfo have each key once.
        PackageIdentity identity = PackageIdentityFor([]) with
        {
            Locales = [new("zh-Hant", "Hant", null), new("zh-TW", "TW", null), new("es-419", "Notas", "Notas para todos")],
        };
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.Deb);
        string desktop = PackageManifests.Render(identity, "linux/app.desktop", fields);
        Equal(1, Regex.Count(desktop, @"^Name\[zh_TW\]=TW$", RegexOptions.Multiline));
        DoesNotContain(desktop, "=Hant");
        Contains(desktop, "Name[es]=Notas\nComment[es]=Notas para todos\n");
        XDocument component = XDocument.Parse(PackageManifests.Render(identity, "linux/app.metainfo.xml", fields));
        string[] languages = [.. component.Root!.Elements("name").Select(name => (string?)name.Attribute(XNamespace.Xml + "lang")).OfType<string>()];
        Equal("zh_TW,es", string.Join(',', languages));
        ValidateDesktopEntry(desktop);
    }

    // desktop-file-validate (desktop-file-utils), which Flathub runs, when it is on the PATH; the check is skipped
    // otherwise.
    private static void ValidateDesktopEntry(string desktop)
    {
        string? validator = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "desktop-file-validate")).FirstOrDefault(File.Exists);
        if (validator is null) return;
        using var workspace = new TestWorkspace();
        string path = workspace.Write("org.example.Notes.desktop", desktop);
        var start = new System.Diagnostics.ProcessStartInfo(validator, [path]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Equal(0, process.ExitCode);
        DoesNotContain(output, "error:");
    }

    [Test, DisplayName("package declares the application's locales in the bundle, the MSIX and the desktop entry")]
    public async Task PackageApplicationLocaleManifests()
    {
        PackageIdentity identity = PackageIdentityFor([]) with
        {
            Locales = [new("en", null, null), new("de", "Notizen & Co", "Notizen \"für\" alle\nund jeden"), new("pt-BR", "Notas", null)],
        };
        string info = PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS));
        XDocument.Parse(info);
        Contains(info, "<key>CFBundleDevelopmentRegion</key><string>en</string>");
        Contains(info, "<key>CFBundleLocalizations</key><array><string>en</string><string>de</string><string>pt-BR</string></array>");
        Contains(info, "<key>LSHasLocalizedDisplayName</key><true/>");
        MacOSPackager.CheckInfo(info, identity);
        // Without a translated name, Finder has no localized display name to look for.
        PackageIdentity unnamedLocales = identity with { Locales = [new("en", null, null), new("de", null, "Notizen für alle")] };
        DoesNotContain(PackageManifests.Render(unnamedLocales, "macos/Info.plist", PackageManifests.Fields(unnamedLocales, PackageTarget.MacOS)),
            "<key>LSHasLocalizedDisplayName</key>");
        Equal("\"CFBundleName\" = \"a\\\\b\\\"c\\nd\\re\\tf\";\n\"CFBundleDisplayName\" = \"a\\\\b\\\"c\\nd\\re\\tf\";\n",
            PackageManifests.MacOSInfoStrings(new("de", "a\\b\"c\nd\re\tf", null)));

        string appx = PackageManifests.Render(identity, "windows/AppxManifest.xml", PackageManifests.Fields(identity, PackageTarget.Windows, "win-x64"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        Equal("en,de,pt-BR", string.Join(',', XDocument.Parse(appx).Descendants(foundation + "Resource").Select(row => (string?)row.Attribute("Language"))));

        Dictionary<string, string> linux = PackageManifests.Fields(identity, PackageTarget.Deb);
        string desktop = PackageManifests.Render(identity, "linux/app.desktop", linux);
        Contains(desktop, "Name=Notes & Co\nComment=Notes <for> everyone\nName[de]=Notizen & Co\nComment[de]=Notizen \"für\" alle\\nund jeden\nName[pt_BR]=Notas\nExec=");
        ValidateDesktopEntry(desktop);
        string metainfo = PackageManifests.Render(identity, "linux/app.metainfo.xml", linux);
        XDocument component = XDocument.Parse(metainfo);
        XNamespace xml = XNamespace.Xml;
        Equal("Notizen & Co", component.Root!.Elements("name").Single(name => (string?)name.Attribute(xml + "lang") == "de").Value);
        Equal("Notas", component.Root.Elements("name").Single(name => (string?)name.Attribute(xml + "lang") == "pt_BR").Value);
        Equal("Notizen \"für\" alle\nund jeden", component.Root.Elements("summary").Single(summary => (string?)summary.Attribute(xml + "lang") == "de").Value);
        Equal(1, component.Root.Elements("summary").Count(summary => summary.Attribute(xml + "lang") is null));

        using var workspace = new TestWorkspace();
        string resources = Path.Combine(workspace.Root, "Notes.app", "Contents", "Resources");
        await MacOSPackager.WriteLocalizationsAsync(identity, resources, CancellationToken.None);
        Equal("de.lproj,en.lproj,pt-BR.lproj", string.Join(',', Directory.GetDirectories(resources).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
        Equal(0, Directory.GetFiles(Path.Combine(resources, "en.lproj")).Length);
        Equal("\"CFBundleName\" = \"Notizen & Co\";\n\"CFBundleDisplayName\" = \"Notizen & Co\";\n",
            File.ReadAllText(Path.Combine(resources, "de.lproj", "InfoPlist.strings")));
    }

    [Test, DisplayName("package keeps English alone without RunicApplicationLocales, and refuses an override without the locale fields")]
    public void PackageApplicationLocaleDefaultsAndOverrides()
    {
        PackageIdentity english = PackageIdentityFor([]);
        string appx = PackageManifests.Render(english, "windows/AppxManifest.xml", PackageManifests.Fields(english, PackageTarget.Windows, "win-x64"));
        Contains(appx, "<Resources>\n    <Resource Language=\"en-us\" />\n  </Resources>");
        string info = PackageManifests.Render(english, "macos/Info.plist", PackageManifests.Fields(english, PackageTarget.MacOS));
        DoesNotContain(info, "<key>CFBundleLocalizations</key>");
        DoesNotContain(PackageManifests.Render(english, "linux/app.desktop", PackageManifests.Fields(english, PackageTarget.Deb)), "Name[");

        using var workspace = new TestWorkspace();
        PackageIdentity identity = PackageIdentityFor([], packagingDirectory: Path.Combine(workspace.Root, "packaging")) with
        {
            Locales = [new("en", null, null), new("de", "Notizen", null)],
        };
        // Manifests of an earlier template: the locale fields are missing.
        workspace.Write("packaging/windows/AppxManifest.xml", "<Package><Resources><Resource Language=\"en-us\" /></Resources></Package>");
        workspace.Write("packaging/macos/Info.plist", "<plist><dict>{{UrlSchemes}}</dict></plist>");
        workspace.Write("packaging/linux/app.desktop", "[Desktop Entry]\nName={{ApplicationName}}\n");
        workspace.Write("packaging/linux/app.metainfo.xml", "<component><name>{{ApplicationName}}</name>{{LocalizedNames}}<summary>{{Description}}</summary></component>");
        foreach ((string name, PackageTarget target, string field, string where) in new[]
        {
            ("windows/AppxManifest.xml", PackageTarget.Windows, "{{Resources}}",
                "Add {{Resources}} inside <Resources> (<Resources>{{Resources}}</Resources>) and remove the old <Resource Language=\"en-us\" /> row"),
            ("macos/Info.plist", PackageTarget.MacOS, "{{Localizations}}",
                "Add {{Localizations}} inside the top-level <dict>, e.g. right after {{UsageDescriptions}}."),
            ("linux/app.desktop", PackageTarget.Deb, "{{LocalizedEntries}}",
                "Add {{LocalizedEntries}} at the start of a line in the [Desktop Entry] group"),
        })
        {
            var error = Throws<DevDevelopmentException>(() => PackageManifests.Render(identity, name, PackageManifests.Fields(identity, target, "win-x64")));
            Equal(PackageCodes.ValidationFailed, error.Code);
            // The file on disk, the field and where it goes.
            Contains(error.Message, $"The manifest {PackageManifests.FindOverride(identity, name)} has no {field}");
            Contains(error.Message, where);
        }
        // An override that adds {{Resources}} but keeps the old row would declare en-us twice.
        string appxPath = workspace.Write("packaging/windows/AppxManifest.xml",
            "<Package><Resources>{{Resources}}\n    <Resource Language=\"en-us\" /></Resources></Package>");
        var duplicate = Throws<DevDevelopmentException>(() => PackageManifests.Render(identity, "windows/AppxManifest.xml",
            PackageManifests.Fields(identity, PackageTarget.Windows, "win-x64")));
        Equal(PackageCodes.ValidationFailed, duplicate.Code);
        Contains(duplicate.Message, appxPath);
        Contains(duplicate.Message, "Remove the row");
        // An override copied from an earlier template gets the name escaped as a desktop entry string, not as XML.
        workspace.Write("packaging/linux/app.desktop", "[Desktop Entry]\nName={{ApplicationName}}\n{{LocalizedEntries}}");
        Equal("[Desktop Entry]\nName=Notes & Co\nName[de]=Notizen\n",
            PackageManifests.Render(identity, "linux/app.desktop", PackageManifests.Fields(identity, PackageTarget.Deb)));
        // Without descriptions, a metainfo needs no LocalizedSummaries.
        Contains(PackageManifests.Render(identity, "linux/app.metainfo.xml", PackageManifests.Fields(identity, PackageTarget.Deb)),
            "<name xml:lang=\"de\">Notizen</name>");
        // Locales without names need no desktop entry field.
        PackageIdentity unnamed = identity with { Locales = [new("en", null, null), new("de", null, null)] };
        DoesNotContain(PackageManifests.Render(unnamed, "linux/app.desktop", PackageManifests.Fields(unnamed, PackageTarget.Deb)), "[de]");
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

    [Test, DisplayName("package macos signs for the App Sandbox with --sandbox and RunicMacSandbox, and only then")]
    public void PackageMacOSSandboxEntitlements()
    {
        PackageIdentity identity = PackageIdentityFor([]);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.MacOS);
        Equal("", fields["Entitlements"]);
        string plain = PackageManifests.Render(identity, "macos/entitlements.plist", fields);
        MacOSPackager.CheckEntitlements(plain, sandbox: false, profile: null);
        SequenceEqual([], PlistKeys(plain));

        fields["Entitlements"] = MacOSPackager.Entitlements(sandbox: true, profile: null);
        string sandboxed = PackageManifests.Render(identity, "macos/entitlements.plist", fields);
        MacOSPackager.CheckEntitlements(sandboxed, sandbox: true, profile: null);
        SequenceEqual(["com.apple.security.app-sandbox", "com.apple.security.network.server", "com.apple.security.network.client",
            "com.apple.security.files.user-selected.read-write", "com.apple.security.files.bookmarks.app-scope"], PlistKeys(sandboxed));
        XDocument document = XDocument.Parse(sandboxed);
        True(document.Root!.Element("dict")!.Elements().Where(element => element.Name != "key").All(element => element.Name == "true"), sandboxed);

        var profile = new MacOSPackager.ProfileIdentity("ABCDE12345.org.example.Notes", "ABCDE12345");
        fields["Entitlements"] = MacOSPackager.Entitlements(sandbox: true, profile);
        string store = PackageManifests.Render(identity, "macos/entitlements.plist", fields);
        MacOSPackager.CheckEntitlements(store, sandbox: true, profile);
        Contains(store, "<key>com.apple.application-identifier</key><string>ABCDE12345.org.example.Notes</string>");
        Contains(store, "<key>com.apple.developer.team-identifier</key><string>ABCDE12345</string>");

        // An override copied before 0.12 has no {{Entitlements}}: --sandbox fails instead of signing an unsandboxed bundle.
        string old = """<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict></dict></plist>""";
        MacOSPackager.CheckEntitlements(old, sandbox: false, profile: null);
        Contains(Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements(old, sandbox: true, profile: null)).Message, "{{Entitlements}}");
        Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements(old, sandbox: false, profile)).Code);
        // A key the override already has and the field adds again, a sandbox the override turns off, and no plist.
        string twice = """<plist version="1.0"><dict><key>com.apple.security.app-sandbox</key><true/><key>com.apple.security.app-sandbox</key><true/></dict></plist>""";
        Contains(Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements(twice, sandbox: true, profile: null)).Message, "twice");
        string off = """<plist version="1.0"><dict><key>com.apple.security.app-sandbox</key><false/><key>com.apple.security.network.server</key><true/><key>com.apple.security.network.client</key><true/><key>com.apple.security.files.user-selected.read-write</key><true/><key>com.apple.security.files.bookmarks.app-scope</key><true/></dict></plist>""";
        Contains(Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements(off, sandbox: true, profile: null)).Message, "off");
        Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements("<plist", sandbox: false, profile: null)).Code);
    }

    [Test, DisplayName("package macos writes LSApplicationCategoryType from RunicMacApplicationCategory and reads RunicMacSandbox")]
    public void PackageMacOSApplicationCategory()
    {
        PackageIdentity identity = PackageIdentityFor([]);
        DoesNotContain(PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS)), "<key>LSApplicationCategoryType</key>");
        identity = identity with { MacApplicationCategory = "public.app-category.developer-tools" };
        string info = PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS));
        XDocument.Parse(info);
        Contains(info, "<key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>");

        Equal(null, PackageIdentity.CheckApplicationCategory(" "));
        Equal("public.app-category.productivity", PackageIdentity.CheckApplicationCategory(" Public.App-Category.Productivity "));
        foreach (string category in new[] { "productivity", "public.app-category.", "public.app-category.a_b", "public.app-category.games.puzzle", "com.example.category" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.CheckApplicationCategory(category)).Code);

        string project = Path.Combine(Path.GetTempPath(), "Notes", "Notes.csproj");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["ApplicationId"] = "org.example.Notes" };
        PackageIdentity unsandboxed = PackageIdentity.Create(project, properties, [], null);
        False(unsandboxed.MacSandbox, "The bundle is not sandboxed by default (D045.5).");
        Equal(null, unsandboxed.MacApplicationCategory);
        properties["RunicMacSandbox"] = "True";
        properties["RunicMacApplicationCategory"] = "public.app-category.productivity";
        PackageIdentity sandboxed = PackageIdentity.Create(project, properties, [], null);
        True(sandboxed.MacSandbox, "RunicMacSandbox is case-insensitive.");
        Equal("public.app-category.productivity", sandboxed.MacApplicationCategory);
        True(PackageIdentity.Properties.Contains("RunicMacSandbox") && PackageIdentity.Properties.Contains("RunicMacApplicationCategory"), "package evaluates both properties.");
    }

    [Test, DisplayName("package macos reads the provisioning profile's team and application identifier")]
    public void PackageMacOSProvisioningProfile()
    {
        static string Profile(string? team, string? application) => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>AppIDName</key><string>Notes</string>
              <key>Entitlements</key>
              <dict>
                {(application is null ? "" : $"<key>com.apple.application-identifier</key><string>{application}</string>")}
                <key>com.apple.developer.team-identifier</key><string>{team}</string>
                <key>keychain-access-groups</key><array><string>{team}.*</string></array>
              </dict>
              <key>Name</key><string>Notes Mac App Store</string>
              {(team is null ? "" : $"<key>TeamIdentifier</key><array><string>{team}</string></array>")}
            </dict>
            </plist>
            """;
        Equal(new MacOSPackager.ProfileIdentity("ABCDE12345.org.example.Notes", "ABCDE12345"),
            MacOSPackager.ReadProfile(Profile("ABCDE12345", "ABCDE12345.org.example.Notes"), "org.example.Notes"));
        // A wildcard profile grants the bundle's own identifier.
        Equal("ABCDE12345.org.example.Notes", MacOSPackager.ReadProfile(Profile("ABCDE12345", "ABCDE12345.*"), "org.example.Notes").ApplicationIdentifier);
        Equal("ABCDE12345.org.example.Notes", MacOSPackager.ReadProfile(Profile("ABCDE12345", "ABCDE12345.org.example.*"), "org.example.Notes").ApplicationIdentifier);
        foreach ((string? team, string? application) in new[]
        {
            ("ABCDE12345", "ABCDE12345.org.example.Other"), ("ABCDE12345", "ZYXWV98765.org.example.Notes"), ("ABCDE12345", "ZYXWV98765.*"),
            ("ABCDE12345", "ABCDE12345.org.other.*"), (null, "ABCDE12345.org.example.Notes"), ("ABCDE12345", null),
        })
        {
            Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() => MacOSPackager.ReadProfile(Profile(team, application), "org.example.Notes")).Code);
        }
        Equal(PackageCodes.ValidationFailed, Throws<DevDevelopmentException>(() => MacOSPackager.ReadProfile("not a plist", "org.example.Notes")).Code);
        Equal(Path.Combine("/out/Notes.app", "Contents", "embedded.provisionprofile"), MacOSPackager.ProfilePath("/out/Notes.app"));
    }

    [Test, DisplayName("package macos signs nested code inside out, then the bundle, and builds the installer package with these command lines")]
    public void PackageMacOSCommandLines()
    {
        SequenceEqual(["--force", "--options", "runtime", "--entitlements", "/out/entitlements.plist", "--sign", "-", "/out/Notes.app"],
            MacOSBundleLayout.Codesign("-", "/out/entitlements.plist", "/out/Notes.app"));
        NestedCode[] nested =
        [
            new("/out/Notes.app/Contents/MacOS/plugins/libdeep.dylib", MachOKind.Library),
            new("/out/Notes.app/Contents/MacOS/helper", MachOKind.Executable),
            new("/out/Notes.app/Contents/MacOS/libnative.dylib", MachOKind.Library),
        ];
        string identity = "Apple Distribution: Example (TEAMID)";
        IReadOnlyList<IReadOnlyList<string>> commands = MacOSBundleLayout.SigningCommands(identity, "/out/entitlements.plist", "/out/inherit.plist", "/out/Notes.app", nested);
        Equal(4, commands.Count);
        SequenceEqual(["--force", "--options", "runtime", "--timestamp", "--sign", identity, "/out/Notes.app/Contents/MacOS/plugins/libdeep.dylib"], commands[0]);
        // In the sandbox a helper executable inherits the application's sandbox; libraries take no entitlements.
        SequenceEqual(["--force", "--options", "runtime", "--timestamp", "--entitlements", "/out/inherit.plist", "--sign", identity, "/out/Notes.app/Contents/MacOS/helper"], commands[1]);
        SequenceEqual(["--force", "--options", "runtime", "--timestamp", "--sign", identity, "/out/Notes.app/Contents/MacOS/libnative.dylib"], commands[2]);
        SequenceEqual(["--force", "--options", "runtime", "--timestamp", "--entitlements", "/out/entitlements.plist", "--sign", identity, "/out/Notes.app"], commands[3]);
        SequenceEqual(["--force", "--options", "runtime", "--sign", "-", "/out/Notes.app/Contents/MacOS/helper"],
            MacOSBundleLayout.SigningCommands("-", "/out/entitlements.plist", null, "/out/Notes.app", nested)[1]);
        DoesNotContain(string.Join(' ', commands.SelectMany(command => command)), "--deep");
        Contains(MacOSBundleLayout.InheritEntitlements, "<key>com.apple.security.inherit</key><true/>");
        XDocument.Parse(MacOSBundleLayout.InheritEntitlements);
        SequenceEqual(["--component", "/out/Notes.app", "/Applications", "/out/Notes-1.2.3.pkg"],
            MacOSPackager.ProductbuildArguments("/out/Notes.app", null, "/out/Notes-1.2.3.pkg"));
        SequenceEqual(["--component", "/out/Notes.app", "/Applications", "--sign", "3rd Party Mac Developer Installer: Example (TEAMID)", "/out/Notes-1.2.3.pkg"],
            MacOSPackager.ProductbuildArguments("/out/Notes.app", "3rd Party Mac Developer Installer: Example (TEAMID)", "/out/Notes-1.2.3.pkg"));
    }

    [Test, DisplayName("package macos puts code in Contents/MacOS and other published files in Contents/Resources, linked back")]
    public void PackageMacOSBundleLayout()
    {
        // Symbolic links need privileges on Windows; the bundle is made on macOS.
        if (OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        string published = Path.Combine(workspace.Root, "publish");
        static byte[] MachO(uint fileType, bool fat = false)
        {
            var thin = new byte[32];
            BitConverter.GetBytes(0xFEEDFACFu).CopyTo(thin, 0); // little-endian 64-bit, as arm64 writes it
            BitConverter.GetBytes(fileType).CopyTo(thin, 12);
            if (!fat) return thin;
            var file = new byte[64 + thin.Length];
            byte[] BigEndian(uint value) { byte[] bytes = BitConverter.GetBytes(value); Array.Reverse(bytes); return bytes; }
            BigEndian(0xCAFEBABE).CopyTo(file, 0);
            BigEndian(1).CopyTo(file, 4);
            BigEndian(64).CopyTo(file, 16);
            thin.CopyTo(file, 64);
            return file;
        }
        void Write(string relative, byte[] content)
        {
            string path = Path.Combine(published, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        Write("Notes", MachO(2));
        Write("libnative.dylib", MachO(6));
        Write("helper", MachO(2, fat: true));
        Write("appsettings.json", "{}"u8.ToArray());
        Write("www/index.html", "<p>notes</p>"u8.ToArray());
        Write("data/words.txt", "words"u8.ToArray());
        Write("plugins/libdeep.dylib", MachO(8));
        Write("plugins/plugin.json", "{}"u8.ToArray());
        Write("Example.class", [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x41, 0, 0, 0, 0, 0, 0, 0, 0]);

        string app = Path.Combine(workspace.Root, "Notes.app");
        IReadOnlyList<NestedCode> nested = MacOSBundleLayout.Place(published, app, "Notes");
        string macOS = Path.Combine(app, "Contents", "MacOS"), resources = Path.Combine(app, "Contents", "Resources");
        // Deepest first, the main executable not at all: it is signed with the bundle.
        SequenceEqual([Path.Combine(macOS, "plugins", "libdeep.dylib"), Path.Combine(macOS, "helper"), Path.Combine(macOS, "libnative.dylib")],
            [.. nested.Select(code => code.Path)]);
        SequenceEqual([MachOKind.Library, MachOKind.Executable, MachOKind.Library], [.. nested.Select(code => code.Kind)]);
        True(File.Exists(Path.Combine(macOS, "Notes")) && new FileInfo(Path.Combine(macOS, "Notes")).LinkTarget is null, "The executable is in Contents/MacOS.");
        True(File.Exists(Path.Combine(resources, "www", "index.html")) && !Path.Exists(Path.Combine(macOS, "www")), "The frontend is only in Contents/Resources.");
        foreach ((string relative, string target) in new[]
        {
            ("appsettings.json", "../Resources/appsettings.json"), ("data", "../Resources/data"), ("Example.class", "../Resources/Example.class"),
            (Path.Combine("plugins", "plugin.json"), "../../Resources/plugins/plugin.json"),
        })
        {
            Equal(target.Replace('/', Path.DirectorySeparatorChar), new FileInfo(Path.Combine(macOS, relative)).LinkTarget);
            True(Path.Exists(Path.Combine(resources, relative)), $"{relative} is in Contents/Resources.");
        }
        Equal("words", File.ReadAllText(Path.Combine(macOS, "data", "words.txt")));
        Equal(null, new FileInfo(Path.Combine(macOS, "plugins", "libdeep.dylib")).LinkTarget);
        Equal(MachOKind.None, MacOSBundleLayout.Classify(Path.Combine(published, "appsettings.json")));
    }

    [Test, DisplayName("package macos declares system file types with the Alternate or Default rank and exports only its own")]
    public void PackageMacOSFileTypes()
    {
        PackageFileType[] types = [new(".notes", null), new(".txt", null), new(".md", "text/markdown"), new(".png", null), new(".logx", null)];
        PackageIdentity identity = PackageIdentityFor([], types);
        string info = PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS));
        XElement root = XDocument.Parse(info).Root!.Element("dict")!;
        XElement Value(XElement dict, string key) => dict.Elements("key").Single(element => element.Value == key).ElementsAfterSelf().First();
        var documents = Value(root, "CFBundleDocumentTypes").Elements("dict")
            .ToDictionary(dict => Value(dict, "LSItemContentTypes").Element("string")!.Value, dict => Value(dict, "LSHandlerRank").Value);
        Equal("Owner", documents["org.example.Notes.notes"]);
        Equal("Owner", documents["org.example.Notes.logx"]);
        Equal("Alternate", documents["public.plain-text"]);
        Equal("Alternate", documents["net.daringfireball.markdown"]);
        Equal("Alternate", documents["public.png"]);
        string[] Declared(string key) => [.. Value(root, key).Elements("dict").Select(dict => Value(dict, "UTTypeIdentifier").Value)];
        SequenceEqual(["org.example.Notes.notes", "org.example.Notes.logx"], Declared("UTExportedTypeDeclarations"));
        // Markdown is imported, as macOS may not declare it; public.plain-text and public.png are the system's.
        SequenceEqual(["net.daringfireball.markdown"], Declared("UTImportedTypeDeclarations"));
        Contains(info, "<key>UTTypeConformsTo</key><array><string>public.plain-text</string></array>");
        Contains(info, "<key>public.mime-type</key><array><string>text/markdown</string></array>");

        // RunicMacFileTypeIdentifiers names the type of an extension the tool does not know; RunicMacFileTypeHandlerRank
        // makes the application the default handler of types it does not own.
        identity = identity with
        {
            MacFileTypeIdentifiers = PackageIdentity.ParseFileTypeIdentifiers(".logx=com.example.log; .TXT=public.plain-text", types),
            MacFileTypeHandlerRank = PackageIdentity.CheckHandlerRank("default"),
        };
        info = PackageManifests.Render(identity, "macos/Info.plist", PackageManifests.Fields(identity, PackageTarget.MacOS));
        root = XDocument.Parse(info).Root!.Element("dict")!;
        documents = Value(root, "CFBundleDocumentTypes").Elements("dict")
            .ToDictionary(dict => Value(dict, "LSItemContentTypes").Element("string")!.Value, dict => Value(dict, "LSHandlerRank").Value);
        Equal("Default", documents["com.example.log"]);
        Equal("Default", documents["public.plain-text"]);
        Equal("Owner", documents["org.example.Notes.notes"]);
        SequenceEqual(["org.example.Notes.notes"], Declared("UTExportedTypeDeclarations"));
        SequenceEqual(["net.daringfireball.markdown", "com.example.log"], Declared("UTImportedTypeDeclarations"));

        Equal("Alternate", PackageIdentity.CheckHandlerRank(""));
        Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.CheckHandlerRank("Owner")).Code);
        foreach (string entry in new[] { ".logx", ".logx=log", ".other=com.example.other", ".logx=com.example.a;.logx=com.example.b", ".logx=com.example_a" })
            Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => PackageIdentity.ParseFileTypeIdentifiers(entry, types)).Code);
        True(PackageIdentity.Properties.Contains("RunicMacFileTypeIdentifiers") && PackageIdentity.Properties.Contains("RunicMacFileTypeHandlerRank"),
            "package evaluates both properties.");
    }

    [Test, DisplayName("package macos writes the camera and microphone usage descriptions with their device entitlements")]
    public void PackageMacOSUsageDescriptions()
    {
        PackageIdentity identity = PackageIdentityFor([]);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.MacOS);
        string info = PackageManifests.Render(identity, "macos/Info.plist", fields);
        DoesNotContain(info, "UsageDescription</key>");
        SequenceEqual([], MacOSPackager.DeviceEntitlements(identity));
        MacOSPackager.CheckInfo(info, identity);

        string project = Path.Combine(Path.GetTempPath(), "Notes", "Notes.csproj");
        identity = PackageIdentity.Create(project, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicationId"] = "org.example.Notes",
            ["RunicMacCameraUsageDescription"] = "Notes shows your camera in video <notes>.",
            ["RunicMacMicrophoneUsageDescription"] = " Notes records voice notes. ",
        }, [], null);
        fields = PackageManifests.Fields(identity, PackageTarget.MacOS);
        info = PackageManifests.Render(identity, "macos/Info.plist", fields);
        XDocument.Parse(info);
        Contains(info, "<key>NSCameraUsageDescription</key><string>Notes shows your camera in video &lt;notes&gt;.</string>");
        Contains(info, "<key>NSMicrophoneUsageDescription</key><string>Notes records voice notes.</string>");
        MacOSPackager.CheckInfo(info, identity);
        string[] devices = MacOSPackager.DeviceEntitlements(identity);
        SequenceEqual(["com.apple.security.device.camera", "com.apple.security.device.audio-input"], devices);
        foreach (bool sandbox in new[] { false, true })
        {
            fields["Entitlements"] = MacOSPackager.Entitlements(sandbox, profile: null, devices);
            string entitlements = PackageManifests.Render(identity, "macos/entitlements.plist", fields);
            MacOSPackager.CheckEntitlements(entitlements, sandbox, profile: null, devices);
            True(devices.All(PlistKeys(entitlements).Contains), entitlements);
        }
        // An override copied before 0.12 has neither field: the command fails instead of shipping a bundle without them.
        string old = """<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict></dict></plist>""";
        Contains(Throws<DevDevelopmentException>(() => MacOSPackager.CheckInfo(old, identity)).Message, "{{UsageDescriptions}}");
        Contains(Throws<DevDevelopmentException>(() => MacOSPackager.CheckEntitlements(old, sandbox: false, profile: null, devices)).Message, "com.apple.security.device.camera");
    }

    private static string[] PlistKeys(string plist) =>
        [.. XDocument.Parse(plist).Root!.Element("dict")!.Elements("key").Select(key => key.Value)];

    [Test, DisplayName("package checks the host and the runtime identifiers of the target")]
    public void PackageRuntimes()
    {
        SequenceEqual(["win-x64"], PackageApplication.ResolveRuntimes(new PackageOptions(PackageTarget.Windows), "win", "x64"));
        SequenceEqual(["osx-arm64"], PackageApplication.ResolveRuntimes(new PackageOptions(PackageTarget.MacOS), "osx", "arm64"));
        SequenceEqual(["osx-arm64"], PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.MacOS) { Runtimes = PackageOptions.ParseRuntimes("osx-arm64, osx-arm64") }, "osx", "arm64"));
        SequenceEqual(["win-arm64"], PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.Windows) { Runtimes = ["win-arm64"] }, "win", "x64"));
        foreach ((PackageOptions options, string host) in new[]
        {
            (new PackageOptions(PackageTarget.Windows), "linux"),
            (new PackageOptions(PackageTarget.MacOS), "win"),
            (new PackageOptions(PackageTarget.Windows) { Runtimes = ["linux-x64"] }, "win"),
            (new PackageOptions(PackageTarget.Windows) { Runtimes = ["win-x64", "win-arm64"] }, "win"),
            // Intel Macs are not a Runic target: no osx-x64 bundle, and no universal one (0.11 made it with lipo).
            (new PackageOptions(PackageTarget.MacOS) { Runtimes = PackageOptions.ParseRuntimes("osx-arm64,osx-x64") }, "osx"),
            (new PackageOptions(PackageTarget.MacOS) { Runtimes = ["osx-x64"] }, "osx"),
            (new PackageOptions(PackageTarget.MacOS), "osx"),
        })
        {
            Equal(PackageCodes.WrongOperatingSystem, Throws<DevUsageException>(() => PackageApplication.ResolveRuntimes(options, host, "x64")).Code);
        }
        string intel = Throws<DevUsageException>(() => PackageApplication.ResolveRuntimes(new PackageOptions(PackageTarget.MacOS), "osx", "x64")).Message;
        Contains(intel, "Intel Macs");
        Contains(intel, "Publish for osx-arm64.");
        Equal("RAPPCLI1011", Throws<DevUsageException>(() => PackageApplication.ResolveRuntimes(
            new PackageOptions(PackageTarget.Windows) { Runtimes = ["Windows X64"] }, "win", "x64")).Code);
    }

    [Test, DisplayName("package targets are in help and completion, and report RAPPPKG faults in JSON")]
    public void PackageCommandLine()
    {
        (int exitCode, string help) = RunCli("package", "--help");
        Equal(0, exitCode);
        foreach (string target in new[] { "windows", "macos", "flatpak", "appimage", "deb", "rpm", "flathub" }) Contains(help, target);
        (_, string windows) = RunCli("package", "windows", "--help");
        foreach (string option in new[] { "--project", "--configuration", "--runtime", "--output-directory", "-o", "--publisher", "--certificate", "--thumbprint", "--timestamp-url" })
            Contains(windows, option);
        (_, string macos) = RunCli("package", "macos", "--help");
        foreach (string option in new[] { "--sign", "--notary-profile", "osx-arm64", "--sandbox", "--provisioning-profile", "--pkg", "--installer-sign" })
            Contains(macos, option);
        (int completionExit, string completion) = RunCli("completion", "bash");
        Equal(0, completionExit);
        foreach (string word in new[] { "package", "--output-directory", "--notary-profile", "--thumbprint", "--gpg-sign", "appimage", "--git-url", "--git-tag" })
            Contains(completion, word);

        // Conflicting and incomplete signing options are usage errors before any work.
        Equal(2, RunCli("package", "windows", "--certificate", "a.pfx", "--thumbprint", "00").ExitCode);
        Equal(2, RunCli("package", "macos", "--notary-profile", "notary").ExitCode);
        Equal(2, RunCli("package", "macos", "--provisioning-profile", "App.provisionprofile").ExitCode);
        Equal(2, RunCli("package", "macos", "--installer-sign", "3rd Party Mac Developer Installer: Example").ExitCode);

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
            [new("/out/Notes.app", "app", "adhoc"), new("/out/Notes-1.2.3.dmg", "dmg", "none")], [new(PackageCodes.NotNotarized, "not notarized")]);
        Equal(
            """{"target":"macos","identity":{"id":"org.example.Notes","version":"1.2.3"},"packages":[{"path":"/out/Notes.app","format":"app","signing":"adhoc"},{"path":"/out/Notes-1.2.3.dmg","format":"dmg","signing":"none"}],"warnings":[{"code":"RAPPPKG2002","message":"not notarized"}]}""",
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
        Equal(PackageCodes.IdentityInvalid, Throws<DevDevelopmentException>(() => DebPackager.Control(identity with { PackageName = "Notes_App" }, "amd64", 1)).Code);

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

    [Test, DisplayName("package flathub lists the NativeAOT lock file's packages and the npm lock file's tarballs")]
    public void PackageFlathubSources()
    {
        string fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "flathub", "App");
        IReadOnlyList<(FlathubNuGetPackage Package, string ContentHash)> locked =
            FlathubSources.ReadNuGetLock(File.ReadAllText(Path.Combine(fixture, "packages.aot.lock.json")));
        // Every runtime identifier's packages, once, including both NativeAOT compiler packages.
        foreach (string id in new[] { "Microsoft.DotNet.ILCompiler", "runtime.linux-x64.Microsoft.DotNet.ILCompiler", "runtime.linux-arm64.Microsoft.DotNet.ILCompiler", "Microsoft.Extensions.DependencyInjection" })
            Equal(1, locked.Count(entry => entry.Package.Id == id));
        IReadOnlyList<FlathubNuGetPackage> merged = FlathubSources.Merge([.. locked.Select(entry => entry.Package), new("Microsoft.NETCore.App.Runtime.linux-x64", "10.0.12"), new("microsoft.dotnet.ilcompiler", "10.0.12")]);
        Equal(locked.Count + 1, merged.Count);
        string nuget = FlathubSources.NuGetSources([new("Microsoft.DotNet.ILCompiler", "10.0.12")], _ => "ab12");
        Equal("""
            [
              {
                "type": "file",
                "url": "https://api.nuget.org/v3-flatcontainer/microsoft.dotnet.ilcompiler/10.0.12/microsoft.dotnet.ilcompiler.10.0.12.nupkg",
                "sha512": "ab12",
                "dest": "nuget-sources",
                "dest-filename": "microsoft.dotnet.ilcompiler.10.0.12.nupkg"
              }
            ]

            """, nuget);
        // The cache's .nupkg.sha512 holds the hash of the signed file in base64.
        Equal("d27a15fb", FlathubSources.HexFromBase64("0noV+w==\n"));
        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevDevelopmentException>(() => FlathubSources.ReadNuGetLock(
            """{"version":1,"dependencies":{"net10.0":{"A":{"type":"Direct","resolved":"1.0.0"}}}}""")).Code);
        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevDevelopmentException>(() => FlathubSources.ReadNuGetLock(
            """{"version":1,"dependencies":{"net10.0":{"A":{"resolved":"1.0.0","contentHash":"x"}},"net10.0/linux-x64":{"A":{"resolved":"1.0.0","contentHash":"y"}}}}""")).Code);

        using JsonDocument npm = JsonDocument.Parse(FlathubSources.NpmSources(File.ReadAllText(Path.Combine(fixture, "Frontend", "package-lock.json"))));
        JsonElement tarball = npm.RootElement.EnumerateArray().Single();
        Equal("https://registry.npmjs.org/is-number/-/is-number-7.0.0.tgz", tarball.GetProperty("url").GetString());
        True(tarball.GetProperty("sha512").GetString()!.StartsWith("e350a27e483a", StringComparison.Ordinal), npm.RootElement.ToString());
        Equal("flatpak-node/npm-tarballs", tarball.GetProperty("dest").GetString());

        // Packages for another OS or musl are left out; one CPU gets only-arches; workspace links are skipped.
        string packageLock = """
            {"lockfileVersion":3,"packages":{"":{"name":"app"},
              "node_modules/@esbuild/linux-x64":{"version":"0.25.0","resolved":"https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-0.25.0.tgz","integrity":"sha512-AAAA","os":["linux"],"cpu":["x64"]},
              "node_modules/@esbuild/linux-arm64":{"version":"0.25.0","resolved":"https://registry.npmjs.org/@esbuild/linux-arm64/-/linux-arm64-0.25.0.tgz","integrity":"sha512-AAAB","os":["linux"],"cpu":["arm64"]},
              "node_modules/@esbuild/darwin-arm64":{"version":"0.25.0","resolved":"https://registry.npmjs.org/@esbuild/darwin-arm64/-/darwin-arm64-0.25.0.tgz","integrity":"sha512-AAAC","os":["darwin"],"cpu":["arm64"]},
              "node_modules/@rollup/rollup-linux-x64-musl":{"version":"4.0.0","resolved":"https://registry.npmjs.org/x.tgz","integrity":"sha512-AAAD","os":["linux"],"cpu":["x64"],"libc":["musl"]},
              "node_modules/@esbuild/linux-ia32":{"version":"0.25.0","resolved":"https://registry.npmjs.org/y.tgz","integrity":"sha512-AAAE","os":["linux"],"cpu":["ia32"]},
              "node_modules/local":{"resolved":"../local","link":true},
              "node_modules/sha256-only":{"version":"1.0.0","resolved":"https://registry.npmjs.org/z.tgz","integrity":"sha1-AAAA sha256-AAAF"}}}
            """;
        using JsonDocument filtered = JsonDocument.Parse(FlathubSources.NpmSources(packageLock));
        string[] described = [.. filtered.RootElement.EnumerateArray().Select(source =>
            source.GetProperty("dest-filename").GetString() + "|" + (source.TryGetProperty("only-arches", out JsonElement arches) ? arches[0].GetString() : "all")
            + "|" + (source.TryGetProperty("sha256", out _) ? "sha256" : "sha512"))];
        SequenceEqual(["esbuild-linux-x64-0.25.0-000000.tgz|x86_64|sha512", "esbuild-linux-arm64-0.25.0-000001.tgz|aarch64|sha512",
            "sha256-only-1.0.0-000005.tgz|all|sha256"], described);
        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevDevelopmentException>(() => FlathubSources.NpmSources(
            """{"lockfileVersion":3,"packages":{"node_modules/a":{"version":"1.0.0","resolved":"https://registry.npmjs.org/a.tgz","integrity":"sha1-AAAA"}}}""")).Code);
        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevDevelopmentException>(() => FlathubSources.NpmSources(
            """{"lockfileVersion":3,"packages":{"node_modules/a":{"version":"1.0.0","resolved":"git+ssh://git@github.com/a/a.git#abc"}}}""")).Code);
    }

    [Test, DisplayName("package flathub writes a source-build manifest pinned to the lock file's runtime")]
    public void PackageFlathubManifest()
    {
        PackageIdentity identity = PackageIdentityFor(["notes"], [new(".notes", null)], dbus: true);
        string manifest = FlathubPackager.Manifest(
            new FlathubBuild(identity, "https://example.org/notes.git", null, "v2.1.0", "src/Notes App/Notes.csproj", "src/Notes App/Frontend", "10.0.12", true),
            "src/Notes App/icons/icon.png", null);
        foreach (string line in new[]
        {
            "app-id: org.example.Notes\n", "runtime: org.gnome.Platform\nruntime-version: '50'\nsdk: org.gnome.Sdk\n",
            "  - --share=network\n  # The Linux global menu: KDE Plasma's menu registrar (Runic.Desktop, global-menu-permission).\n  - --talk-name=com.canonical.AppMenu.Registrar\n",
            "  - org.freedesktop.Sdk.Extension.dotnet10\n  - org.freedesktop.Sdk.Extension.node24\n",
            "append-path: /usr/lib/sdk/dotnet10/bin:/usr/lib/sdk/node24/bin\n", "RUNIC_RUNTIME: linux-arm64\n",
            "cd ''src/Notes App/Frontend'' && npm ci --offline", "dotnet publish ''src/Notes App/Notes.csproj'' -c Release -r \"$RUNIC_RUNTIME\" -p:PublishAot=true -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true",
            "--source \"$PWD/nuget-sources\"", "      - type: git\n        url: 'https://example.org/notes.git'\n        tag: 'v2.1.0'\n",
            "      - nuget-sources.json\n      - npm-sources.json\n", "install -Dm644 org.example.Notes.mime.xml", "install -Dm644 org.example.Notes.service",
            "path: org.example.Notes.service\n", "''#!/bin/sh\\nexec \"/app/lib/Notes/Notes\" \"$@\"\\n'' > /app/bin/Notes",
            "install -Dm644 ''src/Notes App/icons/icon.png'' /app/share/icons/hicolor/512x512/apps/org.example.Notes.png",
        })
        {
            Contains(manifest, line);
        }
        DoesNotContain(manifest, "commit:");
        DoesNotContain(manifest, "scalable");
        string withoutFrontend = FlathubPackager.Manifest(
            new FlathubBuild(identity, "https://example.org/notes.git", "0123abcd", null, "Notes.csproj", null, "10.0.12", false), "icons/icon.png", "icons/icon.svg");
        DoesNotContain(withoutFrontend, "node24");
        DoesNotContain(withoutFrontend, "npm ci");
        DoesNotContain(withoutFrontend, "      - npm-sources.json");
        Contains(withoutFrontend, "        commit: '0123abcd'\n");
        Contains(withoutFrontend, "install -Dm644 icons/icon.svg /app/share/icons/hicolor/scalable/apps/org.example.Notes.svg");

        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevUsageException>(() => FlathubPackager.Validate(new PackageOptions(PackageTarget.Flathub), "linux")).Code);
        Equal(PackageCodes.FlathubSourcesUnavailable, Throws<DevUsageException>(() => FlathubPackager.Validate(
            new PackageOptions(PackageTarget.Flathub) { GitCommit = "a", GitTag = "b" }, "linux")).Code);
        Equal(PackageCodes.WrongOperatingSystem, Throws<DevUsageException>(() => FlathubPackager.Validate(
            new PackageOptions(PackageTarget.Flathub) { GitTag = "v1", Build = true }, "osx")).Code);
        FlathubPackager.Validate(new PackageOptions(PackageTarget.Flathub) { GitTag = "v1" }, "win");

        // On the command line: --git-url is required, and the commit and tag conflict.
        Equal(2, RunCli("package", "flathub", "--git-tag", "v1").ExitCode);
        Equal(2, RunCli("package", "flathub", "--git-url", "https://example.org/a.git", "--git-tag", "v1", "--git-commit", "a").ExitCode);
        (int exitCode, string output) = RunCli("package", "flathub", "--git-url", "https://example.org/a.git", "--format", "json");
        Equal(2, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        Equal(PackageCodes.FlathubSourcesUnavailable, document.RootElement.GetProperty("fault").GetProperty("code").GetString());
    }

    [Test, DisplayName("package writes the MIME definitions and MimeType= that the Nix builder installs for the same project")]
    public void PackageFileTypesMatchNixBuilder()
    {
        // nix/tests/file-types.nix builds this project with buildRunicApplication (nix/lib/desktop-integration.py) and
        // compares its output with the same expected files.
        string fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "nix", "file-types");
        string project = Path.Combine(fixture, "App", "App.csproj");
        Dictionary<string, string> properties = XDocument.Load(project).Root!.Elements("PropertyGroup").Elements()
            .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
        PackageIdentity identity = PackageIdentity.Create(project, properties, [], null);
        Dictionary<string, string> fields = PackageManifests.Fields(identity, PackageTarget.Deb);
        Equal(File.ReadAllText(Path.Combine(fixture, "expected", "org.example.Notes.xml")), fields["MimeInfo"]);
        Equal(File.ReadAllText(Path.Combine(fixture, "expected", "MimeType.txt")), fields["MimeTypes"]);
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
