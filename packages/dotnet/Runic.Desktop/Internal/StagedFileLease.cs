using Runic.Platform;

namespace Runic.Desktop.Internal;

// A dropped file that had no local path, received into a private staging directory that only this lease uses. It is
// read once: opening the stream deletes the staged copy where the platform lets an open file be unlinked (macOS and
// Linux), and disposing the lease closes the stream and deletes the copy and its directory in any case.
internal sealed class StagedFileLease : IReadFileLease
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directory;
    private Stream? _stream;
    private bool _opened;
    private bool _disposed;

    // path: the staged file, the only entry of directory.
    internal StagedFileLease(string path, string directory)
    {
        _path = path;
        _directory = directory;
        DisplayName = SafeName(Path.GetFileName(path));
    }

    public string DisplayName { get; }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opened) throw new InvalidOperationException("A read lease can be consumed once.");
            _opened = true;
            _stream = new FileStream(_path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });
            if (!OperatingSystem.IsWindows()) DeleteCopy();
            return ValueTask.FromResult(_stream);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stream? stream;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            stream = _stream;
            _stream = null;
        }
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        DeleteCopy();
    }

    private void DeleteCopy()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // A file name without directory separators, control characters or a leading dot-dot, for display and for the Views
    // binding's RunicPromisedFile.Name.
    internal static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var characters = name.Select(static character => char.IsControl(character) || character is '/' or '\\' or ':' ? '_' : character).ToArray();
        var safe = new string(characters).Trim();
        return safe is "" or "." or ".." ? "file" : safe;
    }
}
