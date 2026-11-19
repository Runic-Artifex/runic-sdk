using System.Text.Json;

namespace Runic.Spelling;

/// <summary>What a file of a downloadable dictionary is. Open: a later catalogue may add kinds.</summary>
public enum SpellingCatalogueFileKind
{
    /// <summary>The word list, installed as <c>&lt;name&gt;.dic</c>.</summary>
    Dictionary,

    /// <summary>The affix rules, installed as <c>&lt;name&gt;.aff</c>.</summary>
    Affix,

    /// <summary>The dictionary's readme, which names its source and licence, installed under its own name.</summary>
    Readme,

    /// <summary>A licence text of the dictionary, installed under its own name; a dictionary may have several, as the GPL
    /// version 2 and 3 for one under either.</summary>
    License,
}

/// <summary>A file of a downloadable dictionary, at a fixed commit of its source.</summary>
public sealed class SpellingCatalogueFile
{
    internal SpellingCatalogueFile(SpellingCatalogueFileKind kind, Uri url, string sha256, long size, string? file)
    {
        Kind = kind;
        Url = url;
        Sha256 = sha256;
        Size = size;
        _file = file;
    }

    private readonly string? _file;

    /// <summary>What the file is.</summary>
    public SpellingCatalogueFileKind Kind { get; }

    /// <summary>Its HTTPS address, which names a commit, never a branch.</summary>
    public Uri Url { get; }

    /// <summary>Its SHA-256, in lower-case hexadecimal; a download with another is not installed.</summary>
    public string Sha256 { get; }

    /// <summary>Its size in bytes.</summary>
    public long Size { get; }

    internal string InstalledName(string dictionary) => Kind switch
    {
        SpellingCatalogueFileKind.Dictionary => dictionary + ".dic",
        SpellingCatalogueFileKind.Affix => dictionary + ".aff",
        SpellingCatalogueFileKind.Readme => _file ?? "README.md",
        _ => _file ?? "LICENSE",
    };
}

/// <summary>A dictionary that can be downloaded: its language, its files with their hashes, and its licence.</summary>
public sealed class SpellingCatalogueEntry
{
    internal SpellingCatalogueEntry(string languageTag, string name, string license, Uri source, string commit, IReadOnlyList<SpellingCatalogueFile> files)
    {
        LanguageTag = languageTag;
        Name = name;
        License = license;
        Source = source;
        Commit = commit;
        Files = files;
        Size = files.Sum(file => file.Size);
    }

    /// <summary>The language and region it checks, as a BCP 47 tag, such as <c>de-CH</c> or <c>fr</c> (French as in
    /// France, as its source names it).</summary>
    public string LanguageTag { get; }

    /// <summary>The name it is installed under, such as <c>de_CH</c>.</summary>
    public string Name { get; }

    /// <summary>Its licence as an SPDX expression, such as <c>(GPL-2.0 OR GPL-3.0)</c>. The licence text and the readme
    /// are installed beside it.</summary>
    public string License { get; }

    /// <summary>The repository it comes from.</summary>
    public Uri Source { get; }

    /// <summary>The commit of <see cref="Source"/> its files are taken from.</summary>
    public string Commit { get; }

    /// <summary>The bytes a download fetches: all its files together.</summary>
    public long Size { get; }

    /// <summary>Its files: the dictionary, the affixes, the readme and, where it has them, its licence texts.</summary>
    public IReadOnlyList<SpellingCatalogueFile> Files { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({LanguageTag}, {Size} bytes, {License})";
}

/// <summary>The catalogue of downloadable Hunspell dictionaries that ships in the package: every entry pinned to a commit
/// of its source repository, with each file's SHA-256 and size. <c>eng/spelling/generate-catalogue.mjs</c> generates it
/// from wooorm/dictionaries, and LibreOffice/dictionaries for German.</summary>
public sealed class SpellingCatalogue
{
    private static readonly Lazy<SpellingCatalogue> s_default = new(() =>
    {
        using var stream = typeof(SpellingCatalogue).Assembly.GetManifestResourceStream("Runic.Spelling.catalogue.json")
            ?? throw new InvalidOperationException("Runic.Spelling's catalogue is missing from its assembly.");
        return Read(stream);
    });

    private static readonly System.Buffers.SearchValues<char> s_hexadecimal = System.Buffers.SearchValues.Create("0123456789abcdef");

    private readonly Dictionary<string, SpellingCatalogueEntry> _byName;

    private SpellingCatalogue(IReadOnlyList<SpellingCatalogueEntry> entries)
    {
        Entries = entries;
        _byName = entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
    }

    /// <summary>The catalogue that ships in the package.</summary>
    public static SpellingCatalogue Default => s_default.Value;

    /// <summary>The downloadable dictionaries, by tag.</summary>
    public IReadOnlyList<SpellingCatalogueEntry> Entries { get; }

    /// <summary>The entry installed under <paramref name="name"/>, such as <c>de_CH</c>; <see langword="null"/> when the
    /// catalogue has none.</summary>
    public SpellingCatalogueEntry? Find(string name) => _byName.GetValueOrDefault(name);

    internal bool Contains(SpellingCatalogueEntry entry) => _byName.TryGetValue(entry.Name, out var own) && ReferenceEquals(own, entry);

    // The catalogue's JSON (runic.spelling.catalogue/1). allowLoopback: the tests' catalogue names a local http:// server.
    internal static SpellingCatalogue Read(Stream stream, bool allowLoopback = false)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "runic.spelling.catalogue/1")
            throw new InvalidDataException("The spelling catalogue has an unknown schema.");
        List<SpellingCatalogueEntry> entries = [];
        foreach (var item in root.GetProperty("dictionaries").EnumerateArray())
        {
            List<SpellingCatalogueFile> files = [];
            foreach (var file in item.GetProperty("files").EnumerateArray())
            {
                var kind = file.GetProperty("kind").GetString() switch
                {
                    "dic" => SpellingCatalogueFileKind.Dictionary,
                    "aff" => SpellingCatalogueFileKind.Affix,
                    "readme" => SpellingCatalogueFileKind.Readme,
                    "license" => SpellingCatalogueFileKind.License,
                    _ => (SpellingCatalogueFileKind?)null,
                };
                // A kind this version does not know is not installed.
                if (kind is null) continue;
                var url = new Uri(file.GetProperty("url").GetString()!, UriKind.Absolute);
                if (!IsAllowed(url, allowLoopback)) throw new InvalidDataException($"The spelling catalogue names {url}, which is not an HTTPS address.");
                var sha256 = file.GetProperty("sha256").GetString()!;
                if (sha256.Length != 64 || sha256.AsSpan().ContainsAnyExcept(s_hexadecimal))
                    throw new InvalidDataException($"The spelling catalogue has an invalid SHA-256 for {url}.");
                var installedAs = file.TryGetProperty("file", out var name) ? name.GetString() : null;
                // An installed name is a plain file name, never a path or the dictionary's own files.
                if (installedAs is not null && (installedAs.Length == 0 || installedAs.StartsWith('.') || Path.GetFileName(installedAs) != installedAs ||
                    installedAs.AsSpan().IndexOfAny('/', '\\') >= 0 || installedAs.EndsWith(".dic", StringComparison.OrdinalIgnoreCase) ||
                    installedAs.EndsWith(".aff", StringComparison.OrdinalIgnoreCase) || installedAs == "source.json"))
                    throw new InvalidDataException($"The spelling catalogue installs {url} as '{installedAs}', which is not a plain file name.");
                files.Add(new(kind.Value, url, sha256, file.GetProperty("size").GetInt64(), installedAs));
            }
            if (!files.Any(file => file.Kind == SpellingCatalogueFileKind.Dictionary) || !files.Any(file => file.Kind == SpellingCatalogueFileKind.Affix))
                continue;
            if (files.Select(file => file.InstalledName("")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw new InvalidDataException("The spelling catalogue installs two files of a dictionary under one name.");
            entries.Add(new(item.GetProperty("tag").GetString()!, item.GetProperty("name").GetString()!, item.GetProperty("license").GetString()!,
                new Uri(item.GetProperty("repository").GetString()!), item.GetProperty("commit").GetString()!, files));
        }
        return new(entries);
    }

    internal static bool IsAllowed(Uri url, bool allowLoopback) =>
        url.Scheme == Uri.UriSchemeHttps || (allowLoopback && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);
}
