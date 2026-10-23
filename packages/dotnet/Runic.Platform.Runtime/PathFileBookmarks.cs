using System.Collections.Immutable;
using System.Text;

namespace Runic.Platform.Runtime;

// File bookmarks where the path itself keeps access, as on Windows and Linux (a Flatpak's document portal paths stay
// valid for the application): the bookmark is the path's UTF-8 bytes, and resolving it checks the item still exists.
internal sealed class PathFileBookmarks : IDesktopFileBookmarks
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public ValueTask<PlatformResult<ImmutableArray<byte>>> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(path);
        return ValueTask.FromResult<PlatformResult<ImmutableArray<byte>>>(File.Exists(full) || Directory.Exists(full)
            ? new PlatformResult<ImmutableArray<byte>>.Success([.. Strict.GetBytes(full)])
            : new PlatformResult<ImmutableArray<byte>>.Failed(PlatformFailureCode.NotFound));
    }

    public ValueTask<PlatformResult<IDesktopBookmarkAccess>> ResolveAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default)
    {
        ValidateBookmark(bookmark);
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        try { path = Strict.GetString(bookmark.AsSpan()); }
        catch (DecoderFallbackException) { return Result(new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.InvalidData)); }
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
            return Result(new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.InvalidData));
        return Result(File.Exists(path) || Directory.Exists(path)
            ? new PlatformResult<IDesktopBookmarkAccess>.Success(new Access(path))
            : new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.NotFound));
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use the fully qualified path of a file or directory.", nameof(path));
    }

    internal static void ValidateBookmark(ImmutableArray<byte> bookmark)
    {
        if (bookmark.IsDefaultOrEmpty) throw new ArgumentException("Supply a bookmark from CreateAsync.", nameof(bookmark));
    }

    private static ValueTask<PlatformResult<IDesktopBookmarkAccess>> Result(PlatformResult<IDesktopBookmarkAccess> result) => ValueTask.FromResult(result);

    private sealed class Access(string path) : IDesktopBookmarkAccess
    {
        public string LocalPath => path;
        public bool IsStale => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
