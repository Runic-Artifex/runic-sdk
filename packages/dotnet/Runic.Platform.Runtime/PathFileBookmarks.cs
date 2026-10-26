using System.Collections.Immutable;
using System.Text;

namespace Runic.Platform.Runtime;

// File bookmarks where the path itself keeps access, as on Windows and Linux (a Flatpak's document portal paths stay
// valid for the application): the bookmark is the path's UTF-8 bytes, and resolving it checks the item still exists.
// The classifier tells a document portal path from a host path, so a document saves through the portal's view.
internal sealed class PathFileBookmarks(Func<string, CancellationToken, ValueTask<NativeFileLocation>>? classify = null) : IDesktopFileBookmarks
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
        if (Decode(bookmark) is not { } path) return Result(new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.InvalidData));
        return Result(File.Exists(path) || Directory.Exists(path)
            ? new PlatformResult<IDesktopBookmarkAccess>.Success(new Access(path))
            : new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.NotFound));
    }

    public async ValueTask<PlatformResult<IDocumentFileLease>> OpenDocumentAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default)
    {
        ValidateBookmark(bookmark);
        cancellationToken.ThrowIfCancellationRequested();
        if (Decode(bookmark) is not { } path || Directory.Exists(path)) return new PlatformResult<IDocumentFileLease>.Failed(PlatformFailureCode.InvalidData);
        if (!File.Exists(path)) return new PlatformResult<IDocumentFileLease>.Failed(PlatformFailureCode.NotFound);
        var location = classify is null ? NativeFileLocation.Host : await classify(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return new PlatformResult<IDocumentFileLease>.Success(await DocumentFileLease.CreateAsync(path, new LocalAtomicFileReplacement(location),
                access: null, this, existing: true, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new PlatformResult<IDocumentFileLease>.Failed(PlatformFailureCode.NotFound, FileErrors.Diagnose(error)); }
        catch (UnauthorizedAccessException error) { return new PlatformResult<IDocumentFileLease>.Failed(PlatformFailureCode.PermissionDenied, FileErrors.Diagnose(error)); }
        catch (IOException error) { return new PlatformResult<IDocumentFileLease>.Failed(PlatformFailureCode.IOError, FileErrors.Diagnose(error)); }
    }

    private static string? Decode(ImmutableArray<byte> bookmark)
    {
        string path;
        try { path = Strict.GetString(bookmark.AsSpan()); }
        catch (DecoderFallbackException) { return null; }
        return path.Contains('\0') || !Path.IsPathFullyQualified(path) ? null : path;
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
