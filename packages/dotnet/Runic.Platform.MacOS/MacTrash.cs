using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// The Trash through -[NSFileManager trashItemAtURL:resultingItemURL:error:], as Finder moves items: ~/.Trash for the
// startup volume and .Trashes on other local volumes. A volume without a trash, such as a network share, reports
// NSFeatureUnsupportedError and the item stays where it was. NSFileManager's default manager is thread-safe, so the
// request needs no AppKit event loop.
internal sealed partial class MacTrash : IDesktopTrash
{
    public async ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return (await Task.Run(() => Trash(path), cancellationToken).ConfigureAwait(false)).Result;
    }

    // The resulting path is for tests, which remove what they trashed.
    internal static (PlatformResult<PlatformUnit> Result, string? TrashedPath) Trash(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        using var pool = new Pool();
        nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
        if (TrashItem(manager, Sel("trashItemAtURL:resultingItemURL:error:"), Url(path), out nint resulting, out nint error) != 0)
            return (new PlatformResult<PlatformUnit>.Success(default), resulting == 0 ? null : Text(Send(resulting, Sel("path"))));
        if (error == 0) return (new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other), null);
        var diagnostic = ErrorDiagnostic(error);
        return (Map(diagnostic), null);
    }

    internal static PlatformResult<PlatformUnit> Map(PlatformDiagnostic diagnostic) => (diagnostic.Domain, diagnostic.Code) switch
    {
        // NSFeatureUnsupportedError: the volume has no trash.
        ("NSCocoaErrorDomain", 3328) => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        // NSFileNoSuchFileError, NSFileReadNoSuchFileError; ENOENT, ENOTDIR.
        ("NSCocoaErrorDomain", 4 or 260) or ("NSPOSIXErrorDomain", 2 or 20) => Failed(PlatformFailureCode.NotFound, diagnostic),
        // NSFileReadNoPermissionError, NSFileWriteNoPermissionError, NSFileWriteVolumeReadOnlyError; EPERM, EACCES, EROFS.
        ("NSCocoaErrorDomain", 257 or 513 or 642) or ("NSPOSIXErrorDomain", 1 or 13 or 30) => Failed(PlatformFailureCode.PermissionDenied, diagnostic),
        // NSFileLockingError; EBUSY.
        ("NSCocoaErrorDomain", 255) or ("NSPOSIXErrorDomain", 16) => Failed(PlatformFailureCode.ResourceBusy, diagnostic),
        // NSFileWriteOutOfSpaceError; EIO, ENOSPC, EDQUOT.
        ("NSCocoaErrorDomain", 640) or ("NSPOSIXErrorDomain", 5 or 28 or 69) => Failed(PlatformFailureCode.IOError, diagnostic),
        _ => Failed(PlatformFailureCode.Other, diagnostic),
    };

    private static PlatformResult<PlatformUnit> Failed(PlatformFailureCode code, PlatformDiagnostic diagnostic) =>
        new PlatformResult<PlatformUnit>.Failed(code, diagnostic);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte TrashItem(nint target, nint selector, nint url, out nint resulting, out nint error);
}
