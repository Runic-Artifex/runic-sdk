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
        "RuntimeIdentifiers", "NuGetLockFilePath",
    ];

    /// <summary>Evaluates the project with <c>dotnet msbuild -getProperty</c> and validates what it reads.</summary>
    internal static async Task<PackageIdentity> ReadAsync(
        string dotnet, string project, string configuration, string? publisher, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(project)!;
        CommandResult result = await CommandRunner.RunAsync(dotnet, directory,
            ["msbuild", project, "-nologo", $"-p:Configuration={configuration}", $"-getProperty:{string.Join(',', Properties)}",
                "-getItem:PackageReference", "-getItem:ProjectReference"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevDevelopmentException(PackageCodes.PublishFailed,
                "dotnet msbuild could not evaluate the project's identity.", result.CombinedOutput);
        }
        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
        var properties = json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
        var references = new List<string>();
        if (json.RootElement.TryGetProperty("Items", out JsonElement items))
        {
            foreach (JsonProperty kind in items.EnumerateObject())
            foreach (JsonElement item in kind.Value.EnumerateArray())
            {
                foreach (string metadata in new[] { "Identity", "Filename" })
                {
                    if (item.TryGetProperty(metadata, out JsonElement value) && value.GetString() is { Length: > 0 } text)
                        references.Add(text);
                }
            }
        }
        return Create(project, properties, references, publisher);
    }

    /// <summary>Builds the identity from evaluated properties and the project's references; see <see cref="ReadAsync"/>.</summary>
    internal static PackageIdentity Create(
        string project, IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> references, string? publisher)
    {
        string Get(string name) => properties.TryGetValue(name, out string? value) ? value.Trim() : "";
        string directory = Path.GetDirectoryName(project)!;
        string assemblyName = Get("AssemblyName").Length != 0 ? Get("AssemblyName") : Path.GetFileNameWithoutExtension(project);
        string title = Get("ApplicationTitle").Length != 0 ? Get("ApplicationTitle") : assemblyName;
        string Resolve(string value, string fallback) =>
            Path.GetFullPath(value.Length != 0 ? value : fallback, directory);
        string lockFile = Resolve(Get("NuGetLockFilePath"), "packages.lock.json");
        return new PackageIdentity
        {
            ProjectPath = project,
            Id = CheckApplicationId(Get("ApplicationId")),
            Name = title,
            Version = Get("Version").Length != 0 ? Get("Version") : "1.0.0",
            UrlSchemes = ParseUrlSchemes(Get("RunicApplicationUrlSchemes")),
            FileTypes = ParseFileTypes(Get("RunicApplicationFileTypes")),
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
        };
    }

    /// <summary>
    /// Every platform accepts this ID: a D-Bus and Flatpak name, a bundle identifier and an MSIX package name.
    /// </summary>
    internal static string CheckApplicationId(string id)
    {
        if (id.Length > 50 || !ApplicationIdSyntax().IsMatch(id))
        {
            throw new DevUsageException(PackageCodes.IdentityInvalid,
                $"ApplicationId '{id}' must be a reverse-DNS name of ASCII letters and digits, each part starting with a letter, at most 50 characters (org.example.Notes).");
        }
        return id;
    }

    /// <summary>
    /// A deb and rpm package name: lower-case letters, digits, '+', '-' and '.', starting with a letter or digit.
    /// The deb and rpm targets check it; the other targets do not use it.
    /// </summary>
    internal static string CheckPackageName(string name)
    {
        if (name.Length < 2 || !PackageNameSyntax().IsMatch(name))
        {
            throw new DevUsageException(PackageCodes.IdentityInvalid,
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
                throw new DevUsageException(PackageCodes.IdentityInvalid,
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
                throw new DevUsageException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationFileTypes has '{entry}', which is not an extension with its dot, such as .notes or .notes=application/x-example-notes.");
            }
            string? mimeType = parts.Length == 2 ? parts[1] : null;
            if (mimeType is not null && !MimeTypeSyntax().IsMatch(mimeType))
            {
                throw new DevUsageException(PackageCodes.IdentityInvalid,
                    $"RunicApplicationFileTypes has '{entry}', whose MIME type is not type/subtype.");
            }
            if (types.Any(type => type.Extension == extension))
                throw new DevUsageException(PackageCodes.IdentityInvalid, $"RunicApplicationFileTypes has {extension} twice.");
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
                throw new DevUsageException(PackageCodes.IdentityInvalid,
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

    [GeneratedRegex("[^A-Za-z0-9.+~]")]
    private static partial Regex LinuxVersionCharacters();
}
