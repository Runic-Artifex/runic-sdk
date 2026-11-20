using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Runic.Spelling;

// A file that a downloaded dictionary must have, with its size and SHA-256 (lowercase hex).
internal sealed record InstalledFile(string Path, long Size, string Sha256);

// A downloaded dictionary is used only while its .dic and .aff are the files it was installed with (DEV-SEC-87): a crash
// after the install, or a later change, can leave other or truncated files under its name, which would otherwise load
// and check spelling wrongly, and never be offered for download again. Each file's size is compared at every look, and
// its SHA-256 once per version of the file (path, modification time and length) in the process.
internal static class InstalledDictionaryCheck
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Written, long Length, string Sha256), bool> s_hashed = new();

    // The dictionary's .dic and .aff with the size and SHA-256 they must have, or null when its source.json does not say
    // what was installed. The catalog's pin when the dictionary was installed from it; one of another pin, which an
    // application with another release of this package installed in the directory all Runic applications of the user
    // share, by the record that its download wrote beside it from its own catalog.
    internal static IReadOnlyList<InstalledFile>? Expected(string folder, string name, SpellingCatalog catalog)
    {
        if (Recorded(folder, name) is not { } recorded) return null;
        if (catalog.Find(name) is { } entry && entry.Commit == recorded.Commit)
            return [.. entry.Files.Where(file => file.Kind is SpellingCatalogFileKind.Dictionary or SpellingCatalogFileKind.Affix)
                .Select(file => new InstalledFile(Path.Combine(folder, file.InstalledName(name)), file.Size, file.Sha256))];
        return recorded.Files;
    }

    // Whether each file has its size and SHA-256. A file that cannot be read is not intact; that answer is not kept.
    internal static bool IsIntact(IReadOnlyList<InstalledFile> files)
    {
        foreach (var file in files)
        {
            FileInfo info = new(file.Path);
            if (!info.Exists || info.Length != file.Size) return false;
            var key = (info.FullName, info.LastWriteTimeUtc, info.Length, file.Sha256);
            if (!s_hashed.TryGetValue(key, out var same))
            {
                try
                {
                    using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                    same = Convert.ToHexStringLower(SHA256.HashData(stream)) == file.Sha256;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
                s_hashed[key] = same;
            }
            if (!same) return false;
        }
        return true;
    }

    // source.json's commit and the .dic and .aff it lists, or null when it is missing, unreadable or names another
    // dictionary.
    private static (string Commit, IReadOnlyList<InstalledFile> Files)? Recorded(string folder, string name)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(folder, "source.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024) return null;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var recordedName) || recordedName.ValueKind != JsonValueKind.String || recordedName.GetString() != name
                || !root.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return null;
            InstalledFile? dictionary = null, affix = null;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object
                    || !file.TryGetProperty("file", out var installedAs) || installedAs.ValueKind != JsonValueKind.String
                    || !file.TryGetProperty("sha256", out var sha256) || sha256.ValueKind != JsonValueKind.String
                    || !file.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes < 0)
                    continue;
                var hash = sha256.GetString()!;
                if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower)) continue;
                var fileName = installedAs.GetString();
                // Only the two names the dictionary is read from: never a path of the record's choosing.
                if (fileName == name + ".dic") dictionary = new(Path.Combine(folder, fileName), bytes, hash);
                else if (fileName == name + ".aff") affix = new(Path.Combine(folder, fileName), bytes, hash);
            }
            return dictionary is null || affix is null ? null : (commit.GetString()!, [dictionary, affix]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
