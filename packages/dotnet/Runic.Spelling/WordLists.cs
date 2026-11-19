using System.Collections.Concurrent;
using System.Text;
using WeCantSpell.Hunspell;

namespace Runic.Spelling;

// Each dictionary read once and kept while the application runs, by its files and their times: every checker of a
// language shares it, and a dictionary installed again over the same path is read again.
internal static class WordLists
{
    private static readonly ConcurrentDictionary<(string Dictionary, string Affix, DateTime Written, long Length), Lazy<WordList>> s_loaded = new();
    private static int s_codePages;

    internal static WordList Load(SpellingDictionary dictionary)
    {
        var file = new FileInfo(dictionary.DictionaryPath);
        var key = (dictionary.DictionaryPath, dictionary.AffixPath, file.Exists ? file.LastWriteTimeUtc : default, file.Exists ? file.Length : -1);
        var lazy = s_loaded.GetOrAdd(key, static key => new Lazy<WordList>(() => Read(key.Dictionary, key.Affix)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Another try may find the files mended.
            s_loaded.TryRemove(new KeyValuePair<(string, string, DateTime, long), Lazy<WordList>>(key, lazy));
            throw;
        }
    }

    private static WordList Read(string dictionary, string affix)
    {
        if (NeedsCodePages(affix)) RegisterCodePages();
        return WordList.CreateFromFiles(dictionary, affix);
    }

    // The .aff file's SET names its encoding. .NET knows UTF-8, UTF-16, ASCII and ISO 8859-1 (Latin-1) itself; another,
    // such as ISO8859-15 or KOI8-R, needs the code pages provider.
    internal static bool NeedsCodePages(string affix)
    {
        try
        {
            using var reader = new StreamReader(affix, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
            for (var line = 0; line < 200 && reader.ReadLine() is { } text; line++)
            {
                var trimmed = text.AsSpan().Trim();
                if (!trimmed.StartsWith("SET", StringComparison.Ordinal) || trimmed.Length < 4 || !char.IsWhiteSpace(trimmed[3])) continue;
                var name = trimmed[3..].Trim().ToString().ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal);
                return name is not ("UTF-8" or "UTF8" or "UTF-16" or "US-ASCII" or "ASCII" or "ISO8859-1" or "ISO-8859-1" or "LATIN1");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private static void RegisterCodePages()
    {
        if (Interlocked.Exchange(ref s_codePages, 1) == 0) Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
