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
    internal static string Render(PackageIdentity identity, string name, IReadOnlyDictionary<string, string> fields) =>
        Fill(Read(identity, name), fields, name);

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
                break;
            case PackageTarget.MacOS:
                fields["BundleVersion"] = PackageIdentity.NumericVersion(identity.Version, 3);
                fields["UrlSchemes"] = MacOSUrlSchemes(identity);
                fields["DocumentTypes"] = MacOSDocumentTypes(identity, name);
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

    private static string MacOSUrlSchemes(PackageIdentity identity) => identity.UrlSchemes.Count == 0 ? "" : $"""

  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>{identity.Id}</string>
      <key>CFBundleURLSchemes</key><array>{string.Concat(identity.UrlSchemes.Select(scheme => $"<string>{scheme}</string>"))}</array>
    </dict>
  </array>
""";

    // Each file type is the application's own: a document type and the uniform type identifier it exports.
    private static string MacOSDocumentTypes(PackageIdentity identity, string name)
    {
        if (identity.FileTypes.Count == 0) return "";
        var text = new StringBuilder("\n  <key>CFBundleDocumentTypes</key>\n  <array>");
        foreach (PackageFileType type in identity.FileTypes)
        {
            text.Append(CultureInfo.InvariantCulture, $"""

    <dict>
      <key>CFBundleTypeName</key><string>{name} document ({type.Extension})</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>LSItemContentTypes</key><array><string>{PackageIdentity.TypeIdentifier(identity.Id, type.Extension)}</string></array>
    </dict>
""");
        }
        text.Append("\n  </array>\n  <key>UTExportedTypeDeclarations</key>\n  <array>");
        foreach (PackageFileType type in identity.FileTypes)
        {
            string mime = type.MimeType is null ? "" : $"\n        <key>public.mime-type</key><array><string>{EscapeXml(type.MimeType)}</string></array>";
            text.Append(CultureInfo.InvariantCulture, $"""

    <dict>
      <key>UTTypeIdentifier</key><string>{PackageIdentity.TypeIdentifier(identity.Id, type.Extension)}</string>
      <key>UTTypeDescription</key><string>{name} document ({type.Extension})</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string></array>
      <key>UTTypeTagSpecification</key>
      <dict>
        <key>public.filename-extension</key><array><string>{type.Extension[1..]}</string></array>{mime}
      </dict>
    </dict>
""");
        }
        text.Append("\n  </array>");
        return text.ToString();
    }

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
