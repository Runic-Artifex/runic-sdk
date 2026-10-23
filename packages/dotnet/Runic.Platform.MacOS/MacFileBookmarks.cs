using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// NSURL bookmarks: security-scoped inside the App Sandbox, whose access starts with startAccessingSecurityScopedResource
// and ends when the access is disposed; ordinary bookmarks, which also follow a moved file, outside it. NSURL's
// bookmark methods are thread-safe, so requests run on the caller's thread without the AppKit event loop.
internal sealed partial class MacFileBookmarks : IDesktopFileBookmarks
{
    private const nuint CreationWithSecurityScope = 1 << 11, ResolutionWithoutUi = 1 << 8, ResolutionWithSecurityScope = 1 << 10;
    // NSCocoaErrorDomain codes.
    private const long NoSuchFile = 4, ReadNoSuchFile = 260, ReadNoPermission = 257, FileReadCorruptFile = 259;

    private static readonly Lazy<bool> Loaded = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _) && Class("NSURL") != 0);

    // The App Sandbox sets the container ID in every sandboxed process's environment.
    internal static bool IsSandboxed => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID"));

    public ValueTask<PlatformResult<ImmutableArray<byte>>> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        PathFileBookmarks.ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Loaded.Value) return Result<ImmutableArray<byte>>(new PlatformResult<ImmutableArray<byte>>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        nint data = CreateBookmark(Url(Path.GetFullPath(path)), Sel("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:"),
            IsSandboxed ? CreationWithSecurityScope : 0, 0, 0, out nint error);
        if (data == 0) return Result(Failure<ImmutableArray<byte>>(error));
        int length = checked((int)Send(data, Sel("length")));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, length);
        return Result<ImmutableArray<byte>>(new PlatformResult<ImmutableArray<byte>>.Success(ImmutableCollectionsMarshal.AsImmutableArray(bytes)));
    }

    public unsafe ValueTask<PlatformResult<IDesktopBookmarkAccess>> ResolveAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default)
    {
        PathFileBookmarks.ValidateBookmark(bookmark);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Loaded.Value) return Result<IDesktopBookmarkAccess>(new PlatformResult<IDesktopBookmarkAccess>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        nint data;
        fixed (byte* bytes = bookmark.AsSpan()) data = Bytes(Class("NSData"), Sel("dataWithBytes:length:"), bytes, (nuint)bookmark.Length);
        bool sandboxed = IsSandboxed;
        byte stale = 0;
        nint url = ResolveBookmark(Class("NSURL"), Sel("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"), data,
            ResolutionWithoutUi | (sandboxed ? ResolutionWithSecurityScope : 0), 0, &stale, out nint error);
        if (url == 0) return Result(Failure<IDesktopBookmarkAccess>(error));
        string path = Text(Send(url, Sel("path")));
        if (path.Length == 0) return Result<IDesktopBookmarkAccess>(new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.InvalidData));
        // Outside the sandbox there is no scope to start: the process already has the user's access.
        if (sandboxed && Bool(url, Sel("startAccessingSecurityScopedResource")) == 0)
            return Result<IDesktopBookmarkAccess>(new PlatformResult<IDesktopBookmarkAccess>.Failed(PlatformFailureCode.PermissionDenied));
        Send(url, Sel("retain"));
        return Result<IDesktopBookmarkAccess>(new PlatformResult<IDesktopBookmarkAccess>.Success(new Access(url, path, stale != 0, sandboxed)));
    }

    private static PlatformResult<T> Failure<T>(nint error)
    {
        if (error == 0) return new PlatformResult<T>.Failed(PlatformFailureCode.Unknown);
        var diagnostic = ErrorDiagnostic(error);
        var code = diagnostic.Domain != "NSCocoaErrorDomain" ? PlatformFailureCode.IOError : diagnostic.Code switch
        {
            NoSuchFile or ReadNoSuchFile => PlatformFailureCode.NotFound,
            ReadNoPermission => PlatformFailureCode.PermissionDenied,
            FileReadCorruptFile => PlatformFailureCode.InvalidData,
            _ => PlatformFailureCode.IOError,
        };
        return new PlatformResult<T>.Failed(code, diagnostic);
    }

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);

    private sealed class Access(nint url, string path, bool stale, bool scoped) : IDesktopBookmarkAccess
    {
        private nint _url = url;
        public string LocalPath => path;
        public bool IsStale => stale;

        public ValueTask DisposeAsync()
        {
            nint value = Interlocked.Exchange(ref _url, 0);
            if (value != 0)
            {
                if (scoped) Send(value, Sel("stopAccessingSecurityScopedResource"));
                Release(value);
            }
            return ValueTask.CompletedTask;
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint CreateBookmark(nint target, nint selector, nuint options, nint keys, nint relativeTo, out nint error);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static unsafe partial nint ResolveBookmark(nint target, nint selector, nint data, nuint options, nint relativeTo, byte* stale, out nint error);
}
