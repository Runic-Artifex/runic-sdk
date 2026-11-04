using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>A file type the application opens: its extension with the dot, in lower case, and an optional MIME type.</summary>
internal sealed record PackageFileType(string Extension, string? MimeType);

/// <summary>
/// A locale the application is translated into (<c>RunicApplicationLocales</c>, <c>RunicApplicationLocale</c> items): a
/// BCP 47 tag of a language, an optional script, region and variants (<c>de</c>, <c>pt-BR</c>, <c>zh-Hant</c>), and the
/// application's name and description in it, when the project gives them.
/// </summary>
internal sealed record PackageLocale(string Tag, string? Name, string? Description);

/// <summary>
/// What <c>dotnet runic package</c> reads from the project: the application's identity, its activation (B16's file
/// types, web link hosts and D-Bus activation) and the packaging properties.
/// </summary>
internal sealed partial record PackageIdentity
{
    internal required string ProjectPath { get; init; }
    internal required string Id { get; init; }
    internal required string Name { get; init; }
    internal required string Version { get; init; }
    internal required IReadOnlyList<string> UrlSchemes { get; init; }
    internal IReadOnlyList<PackageFileType> FileTypes { get; init; } = [];
    internal IReadOnlyList<string> WebLinkHosts { get; init; } = [];
    internal bool DBusActivatable { get; init; }
    internal required string Publisher { get; init; }
    internal required string Description { get; init; }
    internal required string Homepage { get; init; }
    internal required string Executable { get; init; }

    /// <summary>The deb and rpm package name, <c>RunicPackageName</c>; the lower-cased assembly name by default.</summary>
    internal required string PackageName { get; init; }

    /// <summary>The 512 px PNG icon, <c>RunicPackageIcon</c>; <c>icons/icon.png</c> by default.</summary>
    internal required string Icon { get; init; }

    /// <summary>The manifest overrides, <c>RunicPackagingDirectory</c>; <c>packaging/</c> by default.</summary>
    internal required string PackagingDirectory { get; init; }

    /// <summary>Whether the project references Runic.Desktop.Gtk4, which selects GTK 4 and WebKitGTK 6 on Linux.</summary>
    internal bool Gtk4 { get; init; }

    /// <summary>The project's <c>RuntimeIdentifiers</c>.</summary>
    internal IReadOnlyList<string> RuntimeIdentifiers { get; init; } = [];

    /// <summary>The project's NuGet lock file, <c>NuGetLockFilePath</c>, or <c>packages.lock.json</c>.</summary>
    internal string? NuGetLockFile { get; init; }

    /// <summary>The rpm License tag, <c>PackageLicenseExpression</c>; <c>LicenseRef-proprietary</c> by default.</summary>
    internal string License { get; init; } = "LicenseRef-proprietary";

    /// <summary>Whether the macOS bundle is signed for the App Sandbox, <c>RunicMacSandbox</c>; <c>--sandbox</c> also turns it on.</summary>
    internal bool MacSandbox { get; init; }

    /// <summary>The macOS <c>LSApplicationCategoryType</c>, <c>RunicMacApplicationCategory</c>, which the Mac App Store
    /// requires; none by default.</summary>
    internal string? MacApplicationCategory { get; init; }

    /// <summary>
    /// The uniform type identifiers of file types the application does not own, <c>RunicMacFileTypeIdentifiers</c>
    /// (<c>.md=net.daringfireball.markdown</c>), by extension; they add to and replace <see cref="MacFileTypes.Known"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> MacFileTypeIdentifiers { get; init; } = new Dictionary<string, string>();

    /// <summary>The <c>LSHandlerRank</c> for file types the application does not own, <c>RunicMacFileTypeHandlerRank</c>:
    /// <c>Alternate</c>, the default, or <c>Default</c>.</summary>
    internal string MacFileTypeHandlerRank { get; init; } = "Alternate";

    /// <summary>The camera's usage description, <c>RunicMacCameraUsageDescription</c>, which also adds the camera entitlement.</summary>
    internal string? MacCameraUsageDescription { get; init; }

    /// <summary>The microphone's usage description, <c>RunicMacMicrophoneUsageDescription</c>, which also adds the
    /// audio-input entitlement.</summary>
    internal string? MacMicrophoneUsageDescription { get; init; }

    /// <summary>
    /// The locales the application is translated into, the first being its development language: <c>RunicApplicationLocales</c>
    /// (<c>en;de;pt-BR</c>) and the <c>RunicApplicationLocale</c> items, whose <c>Name</c> and <c>Description</c> metadata
    /// give the application's name and description in the locale. Empty by default: the packages declare English only.
    /// </summary>
    internal IReadOnlyList<PackageLocale> Locales { get; init; } = [];

    internal string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>The SVG beside the PNG icon, when there is one.</summary>
    internal string? ScalableIcon
    {
        get
        {
            string svg = Path.ChangeExtension(Icon, ".svg");
            return File.Exists(svg) ? svg : null;
        }
    }

    /// <summary>The MSBuild properties the command reads.</summary>
    internal static readonly string[] Properties =
    [
        "ApplicationId", "ApplicationTitle", "Version", "RunicApplicationUrlSchemes", "RunicApplicationFileTypes",
        "RunicApplicationWebLinkHosts", "RunicApplicationDBusActivatable", "RunicApplicationPublisher", "Description",
        "PackageProjectUrl", "AssemblyName", "RunicPackageIcon", "RunicPackageName", "RunicPackagingDirectory",
        "RuntimeIdentifiers", "NuGetLockFilePath", "PackageLicenseExpression", "RunicMacSandbox", "RunicMacApplicationCategory",
        "RunicMacFileTypeIdentifiers", "RunicMacFileTypeHandlerRank", "RunicMacCameraUsageDescription", "RunicMacMicrophoneUsageDescription",
        "RunicApplicationLocales",
    ];

    /// <summary>Evaluates the project with <c>dotnet msbuild -getProperty</c> and validates what it reads.</summary>
    internal static async Task<PackageIdentity> ReadAsync(
        string dotnet, string project, string configuration, string? publisher, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(project)!;
        CommandResult result = await CommandRunner.RunAsync(dotnet, directory,
            ["msbuild", project, "-nologo", $"-p:Configuration={configuration}", $"-getProperty:{string.Join(',', Properties)}",
                "-getItem:PackageReference", "-getItem:ProjectReference", "-getItem:RunicApplicationLocale"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevDevelopmentException(PackageCodes.PublishFailed,
                "dotnet msbuild could not evaluate the project's identity.", result.CombinedOutput);
        }
        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
        var properties = json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
        var references = new List<string>();
        var locales = new List<PackageLocale>();
        if (json.RootElement.TryGetProperty("Items", out JsonElement items))
        {
            foreach (JsonProperty kind in items.EnumerateObject())
            foreach (JsonElement item in kind.Value.EnumerateArray())
            {
                if (kind.Name == "RunicApplicationLocale")
                {
                    string? Metadata(string name) => item.TryGetProperty(name, out JsonElement value) && value.GetString() is { Length: > 0 } text
                        ? text.Trim() : null;
                    locales.Add(new PackageLocale(Metadata("Identity") ?? "", Metadata("Name"), Metadata("Description")));
                    continue;
                }
                foreach (string metadata in new[] { "Identity", "Filename" })
                {
                    if (item.TryGetProperty(metadata, out JsonElement value) && value.GetString() is { Length: > 0 } text)
                        references.Add(text);
                }
            }
        }
        return Create(project, properties, references, publisher, locales);
    }

    /// <summary>Builds the identity from evaluated properties and the project's references; see <see cref="ReadAsync"/>.</summary>
    internal static PackageIdentity Create(
        string project, IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> references, string? publisher,
        IReadOnlyList<PackageLocale>? localeItems = null)
    {
        string Get(string name) => properties.TryGetValue(name, out string? value) ? value.Trim() : "";
        string directory = Path.GetDirectoryName(project)!;
        string assemblyName = Get("AssemblyName").Length != 0 ? Get("AssemblyName") : Path.GetFileNameWithoutExtension(project);
        string title = Get("ApplicationTitle").Length != 0 ? Get("ApplicationTitle") : assemblyName;
        string Resolve(string value, string fallback) =>
            Path.GetFullPath(value.Length != 0 ? value : fallback, directory);
        string lockFile = Resolve(Get("NuGetLockFilePath"), "packages.lock.json");
        IReadOnlyList<PackageFileType> fileTypes = ParseFileTypes(Get("RunicApplicationFileTypes"));
        return new PackageIdentity
        {
            ProjectPath = project,
            Id = CheckApplicationId(Get("ApplicationId")),
            Name = title,
            Version = Get("Version").Length != 0 ? Get("Version") : "1.0.0",
            UrlSchemes = ParseUrlSchemes(Get("RunicApplicationUrlSchemes")),
            FileTypes = fileTypes,
            WebLinkHosts = ParseWebLinkHosts(Get("RunicApplicationWebLinkHosts")),
            DBusActivatable = string.Equals(Get("RunicApplicationDBusActivatable"), "true", StringComparison.OrdinalIgnoreCase),
            Publisher = !string.IsNullOrWhiteSpace(publisher) ? publisher
                : Get("RunicApplicationPublisher").Length != 0 ? Get("RunicApplicationPublisher") : "CN=Example",
            Description = Get("Description").Length != 0 ? Get("Description") : title,
            Homepage = Get("PackageProjectUrl").Length != 0 ? Get("PackageProjectUrl") : "https://example.com",
            Executable = assemblyName,
            PackageName = Get("RunicPackageName").Length != 0 ? Get("RunicPackageName")
                : NonPackageNameCharacters().Replace(assemblyName.ToLowerInvariant(), "-"),
            Icon = Resolve(Get("RunicPackageIcon"), Path.Combine("icons", "icon.png")),
            PackagingDirectory = Resolve(Get("RunicPackagingDirectory"), "packaging"),
            Gtk4 = references.Any(reference =>
                string.Equals(Path.GetFileNameWithoutExtension(reference), "Runic.Desktop.Gtk4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reference, "Runic.Desktop.Gtk4", StringComparison.OrdinalIgnoreCase)),
            RuntimeIdentifiers = Get("RuntimeIdentifiers").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            NuGetLockFile = lockFile,
            License = Get("PackageLicenseExpression").Length != 0 ? Get("PackageLicenseExpression") : "LicenseRef-proprietary",
            MacSandbox = string.Equals(Get("RunicMacSandbox"), "true", StringComparison.OrdinalIgnoreCase),
            MacApplicationCategory = CheckApplicationCategory(Get("RunicMacApplicationCategory")),
            MacFileTypeIdentifiers = ParseFileTypeIdentifiers(Get("RunicMacFileTypeIdentifiers"), fileTypes),
            MacFileTypeHandlerRank = CheckHandlerRank(Get("RunicMacFileTypeHandlerRank")),
            MacCameraUsageDescription = Get("RunicMacCameraUsageDescription") is { Length: > 0 } camera ? camera : null,
            MacMicrophoneUsageDescription = Get("RunicMacMicrophoneUsageDescription") is { Length: > 0 } microphone ? microphone : null,
            Locales = ParseLocales(Get("RunicApplicationLocales"), localeItems ?? []),
        };
    }

    /// <summary>
    /// The locales of <c>RunicApplicationLocales</c>, then those of the <c>RunicApplicationLocale</c> items, each once, in
    /// their canonical case (<c>pt-BR</c>, <c>zh-Hant</c>). An item gives a locale of the property its name and
    /// description.
    /// </summary>
    internal static IReadOnlyList<PackageLocale> ParseLocales(string? value, IReadOnlyList<PackageLocale> items)
    {
        var locales = new List<PackageLocale>();
        foreach (PackageLocale entry in Split(value).Select(tag => new PackageLocale(tag, null, null)).Concat(items))
        {
            string tag = CanonicalLocale(entry.Tag);
            int index = locales.FindIndex(locale => locale.Tag == tag);
            if (index < 0) locales.Add(entry with { Tag = tag });
            else locales[index] = locales[index] with
            {
                Name = entry.Name ?? locales[index].Name,
                Description = entry.Description ?? locales[index].Description,
            };
        }
        return locales;
    }

    /// <summary>
    /// A language tag every packager reads: <c>ll</c> or <c>lll</c>, then an optional script, region and variants, in the
    /// canonical case of BCP 47: the language and variants in lower case, the script in title case, the region in upper
    /// case (<c>pt-BR</c>, <c>zh-Hant</c>, <c>ca-ES-valencia</c>, <c>es-419</c>).
    /// </summary>
    internal static string CanonicalLocale(string tag)
    {
        Match match = LocaleSyntax().Match(tag.Trim().Replace('_', '-'));
        if (!match.Success)
        {
            throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                $"RunicApplicationLocales has '{tag}', which is not a language tag such as de, pt-BR, zh-Hant or ca-ES-valencia.");
        }
        string language = match.Groups["language"].Value.ToLowerInvariant();
        string script = match.Groups["script"].Value is { Length: 4 } letters
            ? char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant() : "";
        string region = match.Groups["region"].Value.ToUpperInvariant();
        return language + (script.Length == 0 ? "" : "-" + script) + (region.Length == 0 ? "" : "-" + region)
            + string.Concat(match.Groups["variant"].Captures.Select(variant => "-" + variant.Value.ToLowerInvariant()));
    }

    /// <summary>
    /// A locale as desktop entries and AppStream name it, glibc's <c>lang_COUNTRY@modifier</c>, and whether it keeps all of
    /// the tag (<c>Exact</c>). A letter region is the country (<c>pt-BR</c> → <c>pt_BR</c>); a numeric region, which glibc
    /// has no locale for, is left out (<c>es-419</c> → <c>es</c>). The scripts Latn, Cyrl and Deva are the modifiers
    /// <c>@latin</c>, <c>@cyrillic</c> and <c>@devanagari</c> (<c>sr-Latn-RS</c> → <c>sr_RS@latin</c>); <c>zh-Hans</c> and
    /// <c>zh-Hant</c> without a region are <c>zh_CN</c> and <c>zh_TW</c>, and with one the region alone
    /// (<c>zh-Hant-HK</c> → <c>zh_HK</c>); another script is left out. Without a script modifier, the first variant is the
    /// modifier (<c>ca-ES-valencia</c> → <c>ca_ES@valencia</c>). A tag is exact unless a part of it was left out or the
    /// region was inferred from the script.
    /// </summary>
    internal static (string Locale, bool Exact) LinuxLocale(string tag)
    {
        Match match = LocaleSyntax().Match(tag);
        string language = match.Groups["language"].Value;
        string script = match.Groups["script"].Value;
        string region = match.Groups["region"].Value;
        string[] variants = [.. match.Groups["variant"].Captures.Select(variant => variant.Value)];
        bool exact = true;
        if (region.Length == 3)
        {
            region = "";
            exact = false;
        }
        string modifier = script switch { "Latn" => "latin", "Cyrl" => "cyrillic", "Deva" => "devanagari", _ => "" };
        if (script.Length > 0 && modifier.Length == 0)
        {
            if (language != "zh" || script is not ("Hans" or "Hant"))
            {
                exact = false;
            }
            else if (region.Length == 0)
            {
                // zh-Hans and zh-Hant name the region of their glibc locale; with a region, the region alone says enough.
                region = script == "Hans" ? "CN" : "TW";
                exact = false;
            }
        }
        if (variants.Length > 0)
        {
            if (modifier.Length == 0) modifier = variants[0];
            exact &= variants.Length == 1 && modifier == variants[0];
        }
        return (language + (region.Length == 0 ? "" : "_" + region) + (modifier.Length == 0 ? "" : "@" + modifier), exact);
    }

    /// <summary>
    /// The Linux locales of the locales that have a name or a description, each once, in the order they first appear,
    /// with the name and the description of that locale. Where several tags have the same Linux locale (<c>zh-TW</c> and
    /// <c>zh-Hant</c>, <c>es</c> and <c>es-419</c>), the name comes from the first exact one that has a name, else from the
    /// first that has one, and the description likewise; so an explicit region wins over one inferred from the script,
    /// and no desktop entry has the same key twice.
    /// </summary>
    internal static IReadOnlyList<PackageLocale> LinuxLocales(IReadOnlyList<PackageLocale> locales)
    {
        var mapped = locales.Select(locale => (Locale: locale, Linux: LinuxLocale(locale.Tag)))
            .Where(entry => entry.Locale.Name is not null || entry.Locale.Description is not null).ToList();
        return [.. mapped.Select(entry => entry.Linux.Locale).Distinct(StringComparer.Ordinal).Select(key =>
        {
            var candidates = mapped.Where(entry => entry.Linux.Locale == key).OrderBy(entry => entry.Linux.Exact ? 0 : 1)
                .Select(entry => entry.Locale).ToList();
            return new PackageLocale(key, candidates.FirstOrDefault(locale => locale.Name is not null)?.Name,
                candidates.FirstOrDefault(locale => locale.Description is not null)?.Description);
        })];
    }

    /// <summary>
    /// Every platform accepts this ID: a D-Bus and Flatpak name, a bundle identifier and an MSIX package name.
    /// </summary>
    internal static string CheckApplicationId(string id)
    {
        if (id.Length > 50 || !ApplicationIdSyntax().IsMatch(id))
        {
            throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                $"ApplicationId '{id}' must be a reverse-DNS name of ASCII letters and digits, each part starting with a letter, at most 50 characters (org.example.Notes).");
        }
        return id;
    }

    /// <summary>
    /// The macOS application category: empty for none, otherwise a <c>public.app-category.*</c> identifier such as
    /// <c>public.app-category.productivity</c>, in lower case.
    /// </summary>
    internal static string? CheckApplicationCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        string value = category.Trim().ToLowerInvariant();
        if (!ApplicationCategorySyntax().IsMatch(value))
        {
            throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                $"RunicMacApplicationCategory '{category}' must be an Apple application category such as public.app-category.productivity.");
        }
        return value;
    }

    /// <summary>
    /// <c>RunicMacFileTypeIdentifiers</c>: <c>.extension=identifier</c> pairs, each for one of the file types, whose
    /// identifier is a uniform type identifier such as <c>net.daringfireball.markdown</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseFileTypeIdentifiers(string? value, IReadOnlyList<PackageFileType> fileTypes)
    {
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Split(value))
        {
            string[] parts = entry.Split('=');
            string extension = parts[0].ToLowerInvariant();
            if (parts.Length != 2 || !TypeIdentifierSyntax().IsMatch(parts[1]))
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicMacFileTypeIdentifiers has '{entry}', which is not an extension and a uniform type identifier, such as .md=net.daringfireball.markdown.");
            }
            if (!fileTypes.Any(type => type.Extension == extension))
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicMacFileTypeIdentifiers has {extension}, which is not in RunicApplicationFileTypes.");
            }
            if (!identifiers.TryAdd(extension, parts[1]))
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid, $"RunicMacFileTypeIdentifiers has {extension} twice.");
        }
        return identifiers;
    }

    /// <summary><c>RunicMacFileTypeHandlerRank</c>: <c>Alternate</c> (the default) or <c>Default</c>, in any case.</summary>
    internal static string CheckHandlerRank(string? rank)
    {
        if (string.IsNullOrWhiteSpace(rank)) return "Alternate";
        foreach (string allowed in new[] { "Alternate", "Default" })
            if (string.Equals(rank.Trim(), allowed, StringComparison.OrdinalIgnoreCase)) return allowed;
        throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
            $"RunicMacFileTypeHandlerRank '{rank}' must be Alternate or Default: an application owns only its own file types.");
    }

    /// <summary>
    /// A deb and rpm package name: lower-case letters, digits, '+', '-' and '.', starting with a letter or digit.
    /// The deb and rpm targets check it; the other targets do not use it.
    /// </summary>
    internal static string CheckPackageName(string name)
    {
        if (name.Length < 2 || !PackageNameSyntax().IsMatch(name))
        {
            throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                $"RunicPackageName '{name}' must be a package name of lower-case letters, digits, '+', '-' and '.', starting with a letter or digit.");
        }
        return name;
    }

    /// <summary>URL schemes as RFC 3986 allows them, in lower case, which every platform matches.</summary>
    internal static IReadOnlyList<string> ParseUrlSchemes(string? value)
    {
        string[] schemes = [.. Split(value).Select(scheme => scheme.ToLowerInvariant())];
        foreach (string scheme in schemes)
        {
            if (!UrlSchemeSyntax().IsMatch(scheme) || scheme is "http" or "https" or "file")
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationUrlSchemes has '{scheme}', which is not a URL scheme an application can register.");
            }
        }
        return schemes;
    }

    /// <summary>
    /// File types as <c>.notes</c> or <c>.notes=application/x-example-notes</c>, in lower case: an extension every
    /// platform accepts in a manifest, a uniform type identifier and a glob.
    /// </summary>
    internal static IReadOnlyList<PackageFileType> ParseFileTypes(string? value)
    {
        var types = new List<PackageFileType>();
        foreach (string entry in Split(value))
        {
            string[] parts = entry.Split('=');
            string extension = parts[0].ToLowerInvariant();
            if (parts.Length > 2 || extension.Length > 64 || !ExtensionSyntax().IsMatch(extension))
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationFileTypes has '{entry}', which is not an extension with its dot, such as .notes or .notes=application/x-example-notes.");
            }
            string? mimeType = parts.Length == 2 ? parts[1] : null;
            if (mimeType is not null && !MimeTypeSyntax().IsMatch(mimeType))
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationFileTypes has '{entry}', whose MIME type is not type/subtype.");
            }
            if (types.Any(type => type.Extension == extension))
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid, $"RunicApplicationFileTypes has {extension} twice.");
            types.Add(new PackageFileType(extension, mimeType?.ToLowerInvariant()));
        }
        return types;
    }

    /// <summary>Host names whose https links the application opens, in lower case, without a port, path or wildcard.</summary>
    internal static IReadOnlyList<string> ParseWebLinkHosts(string? value)
    {
        string[] hosts = [.. Split(value).Select(host => host.ToLowerInvariant())];
        foreach (string host in hosts)
        {
            if (host.Length > 253 || !HostSyntax().IsMatch(host))
            {
                throw new DevDevelopmentException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationWebLinkHosts has '{host}', which is not a host name such as notes.example.org.");
            }
        }
        return hosts;
    }

    /// <summary>The MIME type a file type has on Linux: its own, or <c>application/x-&lt;last ID part&gt;-&lt;extension&gt;</c>.</summary>
    internal static string LinuxMimeType(string id, PackageFileType type) =>
        type.MimeType ?? $"application/x-{id[(id.LastIndexOf('.') + 1)..].ToLowerInvariant()}-{NonMimeCharacters().Replace(type.Extension[1..], "-")}";

    /// <summary>The uniform type identifier the macOS bundle exports for one of its file types.</summary>
    internal static string TypeIdentifier(string id, string extension) =>
        $"{id}.{NonIdentifierCharacters().Replace(extension[1..], "-")}";

    /// <summary>MSIX and Info.plist need numeric versions: 1.2.3-preview.4 becomes 1.2.3 and 1.2.3.0.</summary>
    internal static string NumericVersion(string? version, int parts)
    {
        string core = (string.IsNullOrEmpty(version) ? "1.0.0" : version).Split('-', '+')[0];
        var numbers = core.Split('.').Select(part => LeadingNumber(part)).ToList();
        while (numbers.Count < parts) numbers.Add(0);
        return string.Join('.', numbers.Take(parts));
    }

    /// <summary>
    /// The version deb and rpm compare correctly: a prerelease sorts before its release with <c>~</c>, so
    /// 1.2.3-preview.4 becomes 1.2.3~preview.4; build metadata is dropped.
    /// </summary>
    internal static string LinuxPackageVersion(string version)
    {
        string withoutMetadata = version.Split('+')[0];
        int dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
        string result = dash < 0 ? withoutMetadata : withoutMetadata[..dash] + "~" + withoutMetadata[(dash + 1)..].Replace('-', '.');
        return LinuxVersionCharacters().Replace(result, ".");
    }

    private static long LeadingNumber(string part)
    {
        int length = 0;
        while (length < part.Length && char.IsAsciiDigit(part[length])) length++;
        return length == 0 ? 0 : long.Parse(part.AsSpan(0, Math.Min(length, 18)), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string[] Split(string? value) =>
        (value ?? "").Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)+$")]
    private static partial Regex ApplicationIdSyntax();

    [GeneratedRegex("^public\\.app-category\\.[a-z]+(-[a-z]+)*$")]
    private static partial Regex ApplicationCategorySyntax();

    [GeneratedRegex("^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial Regex TypeIdentifierSyntax();

    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]+$")]
    private static partial Regex PackageNameSyntax();

    [GeneratedRegex("[^a-z0-9+.-]")]
    private static partial Regex NonPackageNameCharacters();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*$")]
    private static partial Regex UrlSchemeSyntax();

    [GeneratedRegex("^\\.[a-z0-9]+([.+-][a-z0-9]+)*$")]
    private static partial Regex ExtensionSyntax();

    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}$", RegexOptions.IgnoreCase)]
    private static partial Regex MimeTypeSyntax();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex HostSyntax();

    [GeneratedRegex("[^a-z0-9.+-]")]
    private static partial Regex NonMimeCharacters();

    [GeneratedRegex("[^A-Za-z0-9.-]")]
    private static partial Regex NonIdentifierCharacters();

    [GeneratedRegex("^(?<language>[A-Za-z]{2,3})(-(?<script>[A-Za-z]{4}))?(-(?<region>[A-Za-z]{2}|[0-9]{3}))?(-(?<variant>[A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*$")]
    private static partial Regex LocaleSyntax();

    [GeneratedRegex("[^A-Za-z0-9.+~]")]
    private static partial Regex LinuxVersionCharacters();
}
