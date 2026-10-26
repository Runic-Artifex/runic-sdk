using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Saves a file the App Sandbox grants by itself, without a sibling staging file, as NSDocument's safe saving does: the
// bytes are staged in a private item replacement directory on the file's volume (NSItemReplacementDirectory), then
// NSFileManager replaces the file with them in one step, which keeps the file's metadata such as its creation date and
// Finder tags. A new file is renamed into place with renamex_np(RENAME_EXCL), so it never replaces a name that appeared
// since the conflict check. The lease keeps the selection's security-scoped access started meanwhile.
internal sealed partial class MacItemReplacement : IAtomicFileReplacement
{
    private const nuint ItemReplacementDirectory = 99, UserDomainMask = 1; // NSItemReplacementDirectory, NSUserDomainMask
    // NSCocoaErrorDomain codes.
    private const long NoSuchFile = 4, ReadNoPermission = 257, ReadNoSuchFile = 260, WriteNoPermission = 513, WriteVolumeReadOnly = 642;
    private const int EPERM = 1, ENOENT = 2, EIO = 5, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18, EISDIR = 21, EINVAL = 22, EROFS = 30,
        ENOTSUP = 45, ENOTEMPTY = 66;
    private const uint RenameExclusive = 4; // RENAME_EXCL

    private static readonly Lazy<nint> OriginalItemLocationKey = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out nint foundation)
        && NativeLibrary.TryGetExport(foundation, "NSFileOriginalItemLocationKey", out nint key) ? Marshal.ReadIntPtr(key) : 0);

    // The directory each staging file was created in; only these are removed again.
    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.Ordinal);
    private readonly Func<string, string, uint, int>? _rename;

    // The rename seam returns an errno, for tests.
    internal MacItemReplacement(Func<string, string, uint, int>? rename = null) => _rename = rename;

    public bool IsSupported => true;

    public string CreateStagingPath(string targetPath)
    {
        string directory;
        using (new Pool())
        {
            nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
            // Only the volume of the URL counts. A save destination that does not exist yet has none of its own, so
            // its folder stands in for it.
            nint url = ReplacementDirectory(manager, Url(targetPath), out nint error);
            if (url == 0 && !File.Exists(targetPath)) url = ReplacementDirectory(manager, Url(Path.GetDirectoryName(targetPath)!), out error);
            if (url == 0)
                throw new IOException("macOS has no item replacement directory for the file's volume"
                    + (error == 0 ? "." : $" ({ErrorDiagnostic(error)})."));
            directory = Text(Send(url, Sel("path")));
        }
        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory)) throw new IOException("macOS returned no item replacement directory.");
        // The file's own name, so a staged copy left behind by a crash says what it was.
        string staging = Path.Combine(directory, Path.GetFileName(targetPath));
        _directories[staging] = directory;
        return staging;
    }

    public void ReleaseStaging(string stagingPath)
    {
        if (!_directories.TryRemove(stagingPath, out var directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed) =>
        ValueTask.FromResult(existed ? Replace(stagingPath, targetPath) : Create(stagingPath, targetPath));

    private static FileCommitResult Replace(string staging, string target)
    {
        using var pool = new Pool();
        nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
        if (ReplaceItem(manager, Sel("replaceItemAtURL:withItemAtURL:backupItemName:options:resultingItemURL:error:"),
                Url(target), Url(staging), 0, 0, out _, out nint error) != 0)
            return new FileCommitResult.Committed();
        if (error == 0) return new FileCommitResult.CommitUnknown(PlatformFailureCode.Other);
        var diagnostic = ErrorDiagnostic(error);
        // Once the original left its place, neither name may hold what the caller expects.
        nint key = OriginalItemLocationKey.Value;
        nint info = Send(error, Sel("userInfo"));
        if (key == 0 || info != 0 && Arg(info, Sel("objectForKey:"), key) != 0)
            return new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, diagnostic);
        if (diagnostic.Domain != "NSCocoaErrorDomain") return new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, diagnostic);
        return diagnostic.Code switch
        {
            // The file went away after the conflict check: another program removed it.
            NoSuchFile or ReadNoSuchFile => new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict, diagnostic),
            ReadNoPermission or WriteNoPermission or WriteVolumeReadOnly => new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied, diagnostic),
            _ => new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, diagnostic),
        };
    }

    private FileCommitResult Create(string staging, string target)
    {
        int error = Rename(staging, target, RenameExclusive);
        if (error is ENOTSUP or EINVAL)
        {
            // A volume without exclusive rename, such as some network volumes. Re-check and rename; a name created
            // between both steps is replaced.
            if (File.Exists(target) || Directory.Exists(target)) return new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict);
            error = Rename(staging, target, 0);
        }
        return error switch
        {
            0 => new FileCommitResult.Committed(),
            EEXIST or ENOTEMPTY or EISDIR => new FileCommitResult.NotCommitted(PlatformFailureCode.Conflict, PlatformDiagnostic.FromErrno(error)),
            ENOENT => new FileCommitResult.NotCommitted(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(error)),
            // Another volume cannot be renamed onto atomically. Never copy.
            EXDEV or EBUSY => new FileCommitResult.NotCommitted(PlatformFailureCode.ResourceBusy, PlatformDiagnostic.FromErrno(error)),
            EACCES or EPERM or EROFS => new FileCommitResult.NotCommitted(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(error)),
            // A network volume can report an error after it applied the rename.
            EIO => new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, PlatformDiagnostic.FromErrno(error)),
            _ => new FileCommitResult.CommitUnknown(PlatformFailureCode.Other, PlatformDiagnostic.FromErrno(error)),
        };
    }

    private int Rename(string staging, string target, uint flags) =>
        _rename is not null ? _rename(staging, target, flags) : RenameNp(staging, target, flags) == 0 ? 0 : Marshal.GetLastPInvokeError();

    private static nint ReplacementDirectory(nint manager, nint appropriateFor, out nint error) =>
        DirectoryUrl(manager, Sel("URLForDirectory:inDomain:appropriateForURL:create:error:"), ItemReplacementDirectory, UserDomainMask,
            appropriateFor, 1, out error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint DirectoryUrl(nint target, nint selector, nuint directory, nuint domain, nint url, byte create, out nint error);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte ReplaceItem(nint target, nint selector, nint original, nint replacement, nint backupName, nuint options,
        out nint resulting, out nint error);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameNp(string source, string target, uint flags);
}
