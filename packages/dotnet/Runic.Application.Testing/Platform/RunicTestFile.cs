using System.Collections.Immutable;
using System.Text;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// An in-memory file that <see cref="RunicTestFileDialogs"/> hands out: the selection of an open dialog or the
/// destination of a save dialog. It is never on disk.
/// </summary>
/// <remarks>
/// <para>
/// Leases follow the rules of the real ones: a commit replaces the content only when the file still holds what the
/// application last saw, so <see cref="Write(ReadOnlySpan{byte})"/> or <see cref="Delete"/> during a test acts as
/// another writer and makes the next commit report <see cref="PlatformFailureCode.Conflict"/>.
/// </para>
/// <para>
/// <see cref="LocalPath"/> is a fully qualified path that does not exist; read and write the file through its lease or
/// through this object. The type is safe to use from any thread.
/// </para>
/// </remarks>
public sealed class RunicTestFile
{
    private readonly object _gate = new();
    private readonly Queue<(FileCommitResult Result, bool Applied)> _commitResults = new();
    private readonly List<ImmutableArray<byte>> _commits = [];
    private byte[]? _content;

    /// <summary>Creates a file with content, as an existing file the user selects.</summary>
    /// <param name="name">The display name, a filename without a path.</param>
    /// <param name="content">The file's bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains a path separator.</exception>
    public RunicTestFile(string name, ReadOnlySpan<byte> content) : this(name, content.ToArray())
    {
    }

    private RunicTestFile(string name, byte[]? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..")
            throw new ArgumentException("A test file name is a filename, not a path.", nameof(name));
        DisplayName = name;
        LocalPath = Path.Combine(Path.GetTempPath(), "runic-test-files", Guid.NewGuid().ToString("N"), name);
        _content = content;
    }

    /// <summary>Creates a file whose content is <paramref name="text"/> in UTF-8, without a byte order mark.</summary>
    /// <param name="name">The display name, a filename without a path.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The file.</returns>
    public static RunicTestFile FromText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new RunicTestFile(name, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Creates a file that does not exist yet, such as a new save destination.</summary>
    /// <param name="name">The display name, a filename without a path.</param>
    /// <returns>The absent file.</returns>
    public static RunicTestFile Missing(string name) => new(name, content: null);

    /// <summary>The display name, without a path.</summary>
    public string DisplayName { get; }

    /// <summary>A fully qualified path that the file's document leases report; nothing exists there.</summary>
    public string LocalPath { get; }

    /// <summary>Whether the file exists: it was created with content or a commit or <see cref="Write(ReadOnlySpan{byte})"/> created it.</summary>
    public bool Exists
    {
        get { lock (_gate) return _content is not null; }
    }

    /// <summary>A copy of the file's current bytes.</summary>
    /// <returns>The bytes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public byte[] ReadAllBytes()
    {
        lock (_gate) return (byte[]?)_content?.Clone() ?? throw new FileNotFoundException("The test file does not exist.", DisplayName);
    }

    /// <summary>The file's current content as UTF-8 text.</summary>
    /// <returns>The text.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public string ReadAllText() => Encoding.UTF8.GetString(ReadAllBytes());

    /// <summary>The bytes of each committed write, oldest first.</summary>
    public IReadOnlyList<ImmutableArray<byte>> CommittedWrites
    {
        get { lock (_gate) return [.. _commits]; }
    }

    /// <summary>
    /// Whether a lease of the file can begin a write: <see langword="null"/>, the default, lets it write;
    /// <see cref="PlatformUnavailableReason.AtomicReplaceUnavailable"/> acts as a read-only grant.
    /// </summary>
    /// <remarks><see cref="ISaveFileLease.BeginWriteAsync"/> then reports this reason.</remarks>
    public PlatformUnavailableReason? WriteUnavailable { get; set; }

    /// <summary>Replaces the content as another writer would, so the next commit of a lease reports a conflict.</summary>
    /// <param name="content">The new bytes.</param>
    public void Write(ReadOnlySpan<byte> content)
    {
        lock (_gate) _content = content.ToArray();
    }

    /// <summary>Replaces the content with UTF-8 text as another writer would.</summary>
    /// <param name="text">The new text.</param>
    public void WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Deletes the file as another application would.</summary>
    public void Delete()
    {
        lock (_gate) _content = null;
    }

    /// <summary>Scripts the outcome of the next commit that finds no conflict.</summary>
    /// <param name="result">
    /// <see cref="FileCommitResult.NotCommitted"/> leaves the file unchanged; <see cref="FileCommitResult.CommitUnknown"/>
    /// also leaves it unchanged (use <see cref="EnqueueCommitResult(FileCommitResult, bool)"/> for one that landed), and the lease then accepts both the previous and the staged bytes as its own, as a real
    /// lease does; <see cref="FileCommitResult.Committed"/> commits as an unscripted commit would.
    /// </param>
    public void EnqueueCommitResult(FileCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate) _commitResults.Enqueue((result, result is FileCommitResult.Committed));
    }

    /// <summary>Scripts the outcome of the next commit that finds no conflict, and whether its write landed.</summary>
    /// <param name="result">The reported outcome.</param>
    /// <param name="applied">
    /// Whether the staged bytes replace the content. A real lease reports <see cref="FileCommitResult.CommitUnknown"/>
    /// exactly when the write may have landed, so both values are valid for it; <see cref="FileCommitResult.Committed"/>
    /// requires <see langword="true"/> and <see cref="FileCommitResult.NotCommitted"/> <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="applied"/> contradicts <paramref name="result"/>.</exception>
    public void EnqueueCommitResult(FileCommitResult result, bool applied)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is FileCommitResult.Committed && !applied || result is FileCommitResult.NotCommitted && applied)
            throw new ArgumentException($"A {result.GetType().Name} result cannot have applied = {applied}.", nameof(applied));
        lock (_gate) _commitResults.Enqueue((result, applied));
    }

    internal byte[]? Current
    {
        get { lock (_gate) return _content; }
    }

    // Replaces the content when it is still one the lease saw, else reports a conflict.
    internal FileCommitResult Commit(Func<byte[]?, bool> isBaseline, byte[] staged)
    {
        lock (_gate)
        {
            if (!isBaseline(_content)) return new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict);
            var (result, applied) = _commitResults.TryDequeue(out var scripted) ? scripted : (new FileCommitResult.Committed(), true);
            if (applied)
            {
                _content = staged;
                _commits.Add([.. staged]);
            }
            return result;
        }
    }
}
