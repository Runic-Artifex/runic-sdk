using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// The manifests the targets fill: a file in the project's packaging directory (<c>RunicPackagingDirectory</c>) replaces
/// the tool's embedded default, which is the runic-app template's <c>packaging/</c> folder. Both use <c>{{Field}}</c>.
/// </summary>
internal static partial class PackageManifests
{
    /// <summary>The manifests the tool embeds, by their path below the packaging directory.</summary>
    internal static readonly string[] Names =
    [
        "windows/AppxManifest.xml", "macos/Info.plist", "macos/entitlements.plist",
        "linux/app.desktop", "linux/app.metainfo.xml", "linux/app.service", "linux/flatpak.yml",
    ];

    /// <summary>The manifest's override path when the project has one, otherwise null.</summary>
    internal static string? FindOverride(PackageIdentity identity, string name)
    {
        string path = Path.Combine(identity.PackagingDirectory, name.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The project's override of a manifest, or the embedded default.</summary>
    internal static string Read(PackageIdentity identity, string name) =>
        FindOverride(identity, name) is { } path ? File.ReadAllText(path) : ReadDefault(name);

    /// <summary>The embedded default of a manifest.</summary>
    internal static string ReadDefault(string name)
    {
        using Stream stream = typeof(PackageManifests).Assembly.GetManifestResourceStream(ResourceName(name))
            ?? throw new InvalidOperationException($"The tool embeds no manifest {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string ResourceName(string name) => "Runic.Application.Tool.Packaging." + name.Replace('/', '.');

    /// <summary>Replaces <c>{{Name}}</c> with the field; a field the manifest uses but the target does not fill is an error.</summary>
    internal static string Fill(string template, IReadOnlyDictionary<string, string> fields, string name) =>
        FieldReference().Replace(template, match => fields.TryGetValue(match.Groups[1].Value, out string? value)
            ? value
            : throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                $"The manifest {name} uses {{{{{match.Groups[1].Value}}}}}, which dotnet runic package does not fill for this target."));

    /// <summary>Reads and fills a manifest.</summary>
    internal static string Render(PackageIdentity identity, string name, IReadOnlyDictionary<string, string> fields)
    {
        string template = Read(identity, name);
        CheckLocaleFields(identity, name, template);
        if (name == "linux/app.desktop")
        {
            // A desktop entry is not XML: its name and comment are escaped as desktop entry strings (Notes & Co, not
            // Notes &amp; Co), in an override copied from an earlier template too.
            fields = new Dictionary<string, string>(fields, StringComparer.Ordinal)
            {
                ["ApplicationName"] = DesktopValue(identity.Name),
                ["Description"] = DesktopValue(identity.Description),
            };
        }
        return Fill(template, fields, name);
    }

    // RunicApplicationLocales: the fields that carry the locales. A manifest of an earlier template that lacks one fails
    // instead of shipping a package that silently declares English only or drops the translated names.
    private static void CheckLocaleFields(PackageIdentity identity, string name, string template)
    {
        string file = FindOverride(identity, name) ?? name;
        // {{Resources}} writes every Resource row, so a row the override keeps beside it declares a language twice.
        if (name == "windows/AppxManifest.xml" && template.Contains("{{Resources}}", StringComparison.Ordinal)
            && template.Contains("<Resource ", StringComparison.Ordinal))
        {
            throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                $"The manifest {file} has a <Resource> row beside {{{{Resources}}}}, which writes every row. "
                + "Remove the row, e.g. <Resource Language=\"en-us\" />.");
        }
        if (identity.Locales.Count == 0) return;
        bool named = identity.Locales.Any(locale => locale.Name is not null);
        bool described = identity.Locales.Any(locale => locale.Description is not null);
        (string Field, bool Needed, string Where)[] required = name switch
        {
            "macos/Info.plist" => [("Localizations", true, "inside the top-level <dict>, e.g. right after {{UsageDescriptions}}")],
            "windows/AppxManifest.xml" => [("Resources", true,
                "inside <Resources> (<Resources>{{Resources}}</Resources>) and remove the old <Resource Language=\"en-us\" /> row, "
                + "which {{Resources}} replaces")],
            "linux/app.desktop" => [("LocalizedEntries", named || described,
                "at the start of a line in the [Desktop Entry] group, e.g. right before Exec= ({{LocalizedEntries}}Exec=...)")],
            "linux/app.metainfo.xml" => [("LocalizedNames", named, "inside <component>, right after its </name>"),
                ("LocalizedSummaries", described, "inside <component>, right after its </summary>")],
            _ => [],
        };
        foreach ((string field, bool needed, string where) in required)
        {
            if (needed && !template.Contains("{{" + field + "}}", StringComparison.Ordinal))
            {
                throw new DevDevelopmentException(PackageCodes.ValidationFailed,
                    $"The manifest {file} has no {{{{{field}}}}}, which RunicApplicationLocales fills. Add {{{{{field}}}}} {where}.");
            }
        }
    }

    // A desktop entry's string value: one line, with backslash, newline, tab and carriage return escaped (Desktop Entry
    // Specification, "Possible value types").
    private static string DesktopValue(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);

    internal static string EscapeXml(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>The fields every manifest of the target can use, with its URL scheme, file type and web link entries.</summary>
    internal static Dictionary<string, string> Fields(PackageIdentity identity, PackageTarget target, string runtime = "", DateTimeOffset? now = null)
    {
        string name = EscapeXml(identity.Name);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicationId"] = identity.Id,
            ["ApplicationName"] = name,
            ["Description"] = EscapeXml(identity.Description),
            ["Executable"] = identity.Executable,
            ["Version"] = identity.Version,
        };
        switch (target)
        {
            case PackageTarget.Windows:
                fields["Publisher"] = EscapeXml(identity.Publisher);
                fields["Architecture"] = runtime.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "x64";
                fields["PackageVersion"] = PackageIdentity.NumericVersion(identity.Version, 4);
                fields["Extensions"] = WindowsExtensions(identity, name);
                // The languages the package declares; English alone without RunicApplicationLocales, as before.
                fields["Resources"] = identity.Locales.Count == 0 ? "\n    <Resource Language=\"en-us\" />"
                    : string.Concat(identity.Locales.Select(locale => $"\n    <Resource Language=\"{locale.Tag}\" />"));
                break;
            case PackageTarget.MacOS:
                fields["BundleVersion"] = PackageIdentity.NumericVersion(identity.Version, 3);
                fields["UrlSchemes"] = MacOSUrlSchemes(identity);
                fields["DocumentTypes"] = MacOSDocumentTypes(identity, name);
                fields["UsageDescriptions"] = MacOSUsageDescriptions(identity);
                fields["ApplicationCategory"] = identity.MacApplicationCategory is { } category
                    ? $"\n  <key>LSApplicationCategoryType</key><string>{category}</string>" : "";
                // The sandbox and provisioning profile entitlements; MacOSPackager fills them for its options.
                fields["Entitlements"] = "";
                fields["Localizations"] = MacOSLocalizations(identity);
                break;
            default:
                AddLinuxFields(fields, identity, target, name, now ?? ReleaseTime());
                break;
        }
        return fields;
    }

    private static string WindowsExtensions(PackageIdentity identity, string name)
    {
        // URL schemes, file types (all selected files in one process, MultiSelectModel="Player"), app URI handlers for
        // the web link hosts, and the startup task that login startup enables in a package.
        var text = new StringBuilder("\n      <Extensions>");
        foreach (string scheme in identity.UrlSchemes)
        {
            text.Append(CultureInfo.InvariantCulture, $"""

        <uap:Extension Category="windows.protocol">
          <uap:Protocol Name="{scheme}">
            <uap:DisplayName>{name}</uap:DisplayName>
          </uap:Protocol>
        </uap:Extension>
""");
        }
        foreach (PackageFileType type in identity.FileTypes)
        {
            string contentType = type.MimeType is null ? "" : $" ContentType=\"{EscapeXml(type.MimeType)}\"";
            text.Append(CultureInfo.InvariantCulture, $"""

        <uap3:Extension Category="windows.fileTypeAssociation">
          <uap3:FileTypeAssociation Name="{AssociationName().Replace(type.Extension[1..], "-")}" MultiSelectModel="Player">
            <uap:SupportedFileTypes>
              <uap:FileType{contentType}>{type.Extension}</uap:FileType>
            </uap:SupportedFileTypes>
          </uap3:FileTypeAssociation>
        </uap3:Extension>
""");
        }
        if (identity.WebLinkHosts.Count > 0)
        {
            text.Append("""

        <uap3:Extension Category="windows.appUriHandler">
          <uap3:AppUriHandler>
""");
            foreach (string host in identity.WebLinkHosts)
                text.Append(CultureInfo.InvariantCulture, $"\n            <uap3:Host Name=\"{host}\" />");
            text.Append("""

          </uap3:AppUriHandler>
        </uap3:Extension>
""");
        }
        text.Append(CultureInfo.InvariantCulture, $"""

        <desktop:Extension Category="windows.startupTask" Executable="{identity.Executable}.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="LoginStartup" Enabled="false" DisplayName="{name}" />
        </desktop:Extension>
      </Extensions>
""");
        return text.ToString();
    }

    // CFBundleLocalizations lists the languages AppKit may show its own text in (the About panel, open and save panels,
    // the menu items macOS adds); the first is the development region. MacOSPackager adds a <tag>.lproj per locale.
    // LSHasLocalizedDisplayName tells Finder and the Dock to read the name from the InfoPlist.strings of a locale that
    // has a Name.
    private static string MacOSLocalizations(PackageIdentity identity) => identity.Locales.Count == 0 ? "" : $"""

  <key>CFBundleDevelopmentRegion</key><string>{identity.Locales[0].Tag}</string>
  <key>CFBundleLocalizations</key><array>{string.Concat(identity.Locales.Select(locale => $"<string>{locale.Tag}</string>"))}</array>
""" + (identity.Locales.Any(locale => locale.Name is not null) ? "\n  <key>LSHasLocalizedDisplayName</key><true/>" : "");

    /// <summary>
    /// A locale's <c>InfoPlist.strings</c>, which gives the bundle its name in that language, or null without a name.
    /// </summary>
    internal static string? MacOSInfoStrings(PackageLocale locale)
    {
        if (locale.Name is null) return null;
        string name = locale.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"CFBundleName\" = \"{name}\";\n\"CFBundleDisplayName\" = \"{name}\";\n";
    }

    private static string MacOSUrlSchemes(PackageIdentity identity) => identity.UrlSchemes.Count == 0 ? "" : $"""

  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>{identity.Id}</string>
      <key>CFBundleURLSchemes</key><array>{string.Concat(identity.UrlSchemes.Select(scheme => $"<string>{scheme}</string>"))}</array>
    </dict>
  </array>
""";

    // A document type for each file type: the application's own exports its type with the Owner rank; a type the
    // system or another application declares, such as public.plain-text, has the Alternate or Default rank, and its
    // declaration is imported when macOS may not have it (MacFileTypes).
    private static string MacOSDocumentTypes(PackageIdentity identity, string name)
    {
        if (identity.FileTypes.Count == 0) return "";
        MacFileType[] types = [.. identity.FileTypes.Select(type => MacFileTypes.Resolve(identity, type))];
        var text = new StringBuilder("\n  <key>CFBundleDocumentTypes</key>\n  <array>");
        foreach (MacFileType type in types)
        {
            text.Append(CultureInfo.InvariantCulture, $"""

    <dict>
      <key>CFBundleTypeName</key><string>{name} document ({type.Type.Extension})</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>{(type.Owned ? "Owner" : identity.MacFileTypeHandlerRank)}</string>
      <key>LSItemContentTypes</key><array><string>{type.Identifier}</string></array>
    </dict>
""");
        }
        text.Append("\n  </array>");
        AppendTypeDeclarations(text, "UTExportedTypeDeclarations", [.. types.Where(type => type.Owned)], name);
        AppendTypeDeclarations(text, "UTImportedTypeDeclarations", [.. types.Where(type => type.Imported)], name);
        return text.ToString();
    }

    private static void AppendTypeDeclarations(StringBuilder text, string key, MacFileType[] types, string name)
    {
        if (types.Length == 0) return;
        text.Append(CultureInfo.InvariantCulture, $"\n  <key>{key}</key>\n  <array>");
        foreach (MacFileType type in types)
        {
            string mime = type.Type.MimeType is null ? "" : $"\n        <key>public.mime-type</key><array><string>{EscapeXml(type.Type.MimeType)}</string></array>";
            string description = type.Owned ? $"{name} document ({type.Type.Extension})" : $"{type.Type.Extension[1..]} document";
            text.Append(CultureInfo.InvariantCulture, $"""

    <dict>
      <key>UTTypeIdentifier</key><string>{type.Identifier}</string>
      <key>UTTypeDescription</key><string>{EscapeXml(description)}</string>
      <key>UTTypeConformsTo</key><array><string>{type.ConformsTo}</string></array>
      <key>UTTypeTagSpecification</key>
      <dict>
        <key>public.filename-extension</key><array><string>{type.Type.Extension[1..]}</string></array>{mime}
      </dict>
    </dict>
""");
        }
        text.Append("\n  </array>");
    }

    // The privacy usage descriptions macOS shows when the application, or its WKWebView, first uses the camera or the
    // microphone; MacOSPackager adds the matching device entitlements.
    private static string MacOSUsageDescriptions(PackageIdentity identity) =>
        (identity.MacCameraUsageDescription is { } camera ? $"\n  <key>NSCameraUsageDescription</key><string>{EscapeXml(camera)}</string>" : "")
        + (identity.MacMicrophoneUsageDescription is { } microphone ? $"\n  <key>NSMicrophoneUsageDescription</key><string>{EscapeXml(microphone)}</string>" : "");

    private static void AddLinuxFields(Dictionary<string, string> fields, PackageIdentity identity, PackageTarget target, string name, DateTimeOffset now)
    {
        // Where the launcher is: /app/bin in the Flatpak sandbox, /usr/bin in a deb or rpm. An AppImage has no fixed
        // path, so it cannot be D-Bus activated and its entry does not ask for it.
        fields["BinDirectory"] = target == PackageTarget.Flatpak ? "/app/bin" : "/usr/bin";
        fields["ReleaseDate"] = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        fields["Homepage"] = EscapeXml(identity.Homepage);
        string[] mimeTypes =
        [
            .. identity.UrlSchemes.Select(scheme => $"x-scheme-handler/{scheme}"),
            .. identity.FileTypes.Select(type => PackageIdentity.LinuxMimeType(identity.Id, type)),
        ];
        fields["MimeTypes"] = mimeTypes.Length == 0 ? "" : $"MimeType={string.Concat(mimeTypes.Select(type => type + ";"))}\n";
        fields["DBusActivatable"] = identity.DBusActivatable && target != PackageTarget.AppImage ? "DBusActivatable=true\n" : "";
        fields["MimeInfo"] = MimeInfo(identity, name);
        // The name and description in each locale that has them: Name[xx]= and Comment[xx]= in the desktop entry,
        // <name xml:lang> and <summary xml:lang> in the metainfo, one per Linux locale (PackageIdentity.LinuxLocales).
        var entries = new StringBuilder();
        var names = new StringBuilder();
        var summaries = new StringBuilder();
        foreach (PackageLocale locale in PackageIdentity.LinuxLocales(identity.Locales))
        {
            string tag = locale.Tag;
            if (locale.Name is { } localName)
            {
                entries.Append(CultureInfo.InvariantCulture, $"Name[{tag}]={DesktopValue(localName)}\n");
                names.Append(CultureInfo.InvariantCulture, $"\n  <name xml:lang=\"{tag}\">{EscapeXml(localName)}</name>");
            }
            if (locale.Description is { } description)
            {
                entries.Append(CultureInfo.InvariantCulture, $"Comment[{tag}]={DesktopValue(description)}\n");
                summaries.Append(CultureInfo.InvariantCulture, $"\n  <summary xml:lang=\"{tag}\">{EscapeXml(description)}</summary>");
            }
        }
        fields["LocalizedEntries"] = entries.ToString();
        fields["LocalizedNames"] = names.ToString();
        fields["LocalizedSummaries"] = summaries.ToString();
        // The Flatpak installs that only some applications need: the file types' MIME definitions, the D-Bus service file
        // and the scalable icon.
        var extra = new StringBuilder();
        if (identity.FileTypes.Count > 0)
            extra.Append(CultureInfo.InvariantCulture, $"\n      - install -Dm644 {identity.Id}.mime.xml /app/share/mime/packages/{identity.Id}.xml");
        if (identity.DBusActivatable)
            extra.Append(CultureInfo.InvariantCulture, $"\n      - install -Dm644 {identity.Id}.service /app/share/dbus-1/services/{identity.Id}.service");
        if (identity.ScalableIcon is not null)
            extra.Append(CultureInfo.InvariantCulture, $"\n      - install -Dm644 icon.svg /app/share/icons/hicolor/scalable/apps/{identity.Id}.svg");
        fields["ExtraInstalls"] = extra.ToString();
        // The reverse-DNS prefix of the ID, as AppStream names developers.
        fields["DeveloperId"] = identity.Id[..identity.Id.LastIndexOf('.')];
    }

    /// <summary>The file types' shared-mime-info definitions, or empty without file types.</summary>
    internal static string MimeInfo(PackageIdentity identity, string? name = null)
    {
        if (identity.FileTypes.Count == 0) return "";
        name ??= EscapeXml(identity.Name);
        var text = new StringBuilder("""
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
""");
        foreach (PackageFileType type in identity.FileTypes)
        {
            text.Append(CultureInfo.InvariantCulture, $"""

  <mime-type type="{EscapeXml(PackageIdentity.LinuxMimeType(identity.Id, type))}">
    <comment>{name} document ({type.Extension})</comment>
    <glob pattern="*{type.Extension}"/>
  </mime-type>
""");
        }
        text.Append("\n</mime-info>\n");
        return text.ToString();
    }

    /// <summary>The release date of the metainfo: <c>SOURCE_DATE_EPOCH</c> when set, for reproducible packages, otherwise now.</summary>
    internal static DateTimeOffset ReleaseTime() =>
        long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow;

    [GeneratedRegex("\\{\\{([A-Za-z]+)\\}\\}")]
    private static partial Regex FieldReference();

    [GeneratedRegex("[^a-z0-9.-]")]
    private static partial Regex AssociationName();
}
