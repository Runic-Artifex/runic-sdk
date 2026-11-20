using System.Text;

namespace Runic.Spelling;

/// <summary>
/// The user's own words, which every checker given them counts as spelled right: a UTF-8 file of one word per line. By
/// default the file all Runic applications of the user share (<see cref="SharedPath"/>), so a word added in one is known
/// in all; an application may name its own. Safe to use from several threads.
/// </summary>
/// <remarks>Adding and removing read the file again before writing it, so the words another application added since are
/// kept, and write it whole to a temporary file that then replaces it. A file that cannot be read counts as empty; one
/// that cannot be written keeps the change for this process.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore never allocates a wait handle (AvailableWaitHandle is not used), so it holds nothing to dispose.")]
public sealed class SpellingUserWords
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile State _state = new([], KnownWords.Empty);

    private SpellingUserWords(string path) => Path = path;

    /// <summary>The file all Runic applications of the user share: <c>user-words.txt</c> in <c>$XDG_DATA_HOME/runic</c> on
    /// Linux, <c>%LOCALAPPDATA%\Runic</c> on Windows, <c>~/Library/Application Support/Runic</c> on macOS.</summary>
    public static string SharedPath => System.IO.Path.Combine(SpellingFolders.SharedRunicFolder(SpellingEnvironment.Current), "user-words.txt");

    /// <summary>The word list's file.</summary>
    public string Path { get; }

    /// <summary>The words, in ordinal order.</summary>
    public IReadOnlyList<string> Words => _state.Words;

    internal KnownWords Known => _state.Known;

    /// <summary>Reads the word list in <paramref name="path"/>, or <see cref="SharedPath"/>; a missing file is an empty list
    /// that adding a word creates.</summary>
    public static async Task<SpellingUserWords> OpenAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var words = new SpellingUserWords(System.IO.Path.GetFullPath(path ?? SharedPath));
        await words.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return words;
    }

    /// <summary>Whether <paramref name="word"/> is one of the words as written.</summary>
    public bool Contains(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return _state.Words.Contains(word, StringComparer.Ordinal);
    }

    /// <summary>Reads the file again, for words another application changed.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state = State.Of(await ReadAsync(cancellationToken).ConfigureAwait(false) ?? _state.Words);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds <paramref name="word"/>; <see langword="false"/> when it was there.</summary>
    /// <exception cref="ArgumentException"><paramref name="word"/> is empty or not one word.</exception>
    public Task<bool> AddAsync(string word, CancellationToken cancellationToken = default) =>
        ChangeAsync(Validated(word), add: true, cancellationToken);

    /// <summary>Removes <paramref name="word"/>; <see langword="false"/> when it was not there.</summary>
    public Task<bool> RemoveAsync(string word, CancellationToken cancellationToken = default) =>
        ChangeAsync(Validated(word), add: false, cancellationToken);

    private static string Validated(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var trimmed = word.Trim();
        if (trimmed.Length == 0 || trimmed.AsSpan().ContainsAny(" \t\r\n")) throw new ArgumentException("A user's word is one word.", nameof(word));
        return trimmed;
    }

    private async Task<bool> ChangeAsync(string word, bool add, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A file that cannot be read: this process's words.
            var words = new SortedSet<string>(await ReadAsync(cancellationToken).ConfigureAwait(false) ?? _state.Words, StringComparer.Ordinal);
            var changed = add ? words.Add(word) : words.Remove(word);
            _state = State.Of(words);
            if (changed) await WriteAsync(words, cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The file's words: none when there is no file, null when it cannot be read.
    private async Task<IReadOnlyList<string>?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            var lines = await File.ReadAllLinesAsync(Path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return [.. lines.Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteAsync(IEnumerable<string> words, CancellationToken cancellationToken)
    {
        try
        {
            // Only the user reads the list (DEV-SEC-88): each write creates a new file that only the user can read, and
            // moves it over the list at once, so it also keeps that mode.
            var folder = System.IO.Path.GetDirectoryName(Path)!;
            PrivateFiles.CreateDirectory(folder);
            var temporary = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var content = new StringBuilder();
                foreach (var word in words) content.Append(word).Append(Environment.NewLine);
                await PrivateFiles.WriteNewAsync(temporary, new UTF8Encoding(false).GetBytes(content.ToString()), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        // Kept for this process when the file cannot be written.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record State(IReadOnlyList<string> Words, KnownWords Known)
    {
        internal static State Of(IEnumerable<string> words)
        {
            string[] sorted = [.. words.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            return new(sorted, KnownWords.Of(sorted));
        }
    }
}
