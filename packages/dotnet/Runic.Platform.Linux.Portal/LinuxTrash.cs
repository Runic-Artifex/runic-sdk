using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// The freedesktop.org Trash specification 1.0. Outside a sandbox the provider implements it itself, as GIO and KIO do:
// an item on the home trash's file system goes to $XDG_DATA_HOME/Trash; an item elsewhere goes to the trash at the
// top of its mount, $topdir/.Trash/$uid or $topdir/.Trash-$uid. The info file is created exclusively first, then the
// item is renamed into files/; a rename never falls back to copying, so nothing is deleted permanently. Inside Flatpak
// or Snap, where the user's trash is out of reach, the Trash portal moves the item.
internal sealed partial class LinuxTrash(PortalApplication? application = null, string? homeTrash = null, bool? sandboxed = null,
    string? address = null, string destination = "org.freedesktop.portal.Desktop", Func<string, string?>? mountPointOf = null) : IDesktopTrash
{
    private static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(30);
    private bool Sandboxed => sandboxed ?? (File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null);

    public async ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed) return await PortalAsync(path, cancellationToken).ConfigureAwait(false);
        // Renames are quick, but a network file system can block; keep them off the caller's thread.
        return await Task.Run(() => Trash(path), cancellationToken).ConfigureAwait(false);
    }

    private PlatformResult<PlatformUnit> Trash(string path)
    {
        // Resolve links in the parent, not the item: a link is trashed itself.
        string? parent = RealPath(Path.GetDirectoryName(path)!, out int errno);
        if (parent is null) return Failure(errno);
        string item = Path.Combine(parent, Path.GetFileName(path));

        string home = homeTrash ?? HomeTrash();
        var outcome = TrashInto(home, item, item, createTrash: true);
        if (outcome is not Outcome.CrossDevice) return outcome.Result;

        if ((mountPointOf ?? MountPoint)(parent) is not { } topdir) return Unavailable();
        string relative = Path.GetRelativePath(topdir, item);
        uint uid = GetEffectiveUserId();
        // $topdir/.Trash, made by an administrator, sticky and not a link, holds a directory per user.
        string shared = Path.Combine(topdir, ".Trash");
        if (Stat(shared) is { IsDirectory: true, IsLink: false, Sticky: true })
        {
            string own = Path.Combine(shared, uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Prepare(own, uid)) { outcome = TrashInto(own, item, relative, createTrash: false); if (outcome is not Outcome.Unusable) return outcome.Result; }
        }
        string personal = Path.Combine(topdir, ".Trash-" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Prepare(personal, uid)) { outcome = TrashInto(personal, item, relative, createTrash: false); if (outcome is not Outcome.Unusable) return outcome.Result; }
        return Unavailable();
    }

    private static string HomeTrash()
    {
        string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "Trash");
    }

    // A topdir trash this user owns, created with mode 0700 when missing. Anything else, such as a link or a directory
    // another user prepared, is not used: its owner could read what is trashed there.
    private static bool Prepare(string directory, uint uid)
    {
        if (MakeDirectory(directory, 0x1C0) != 0 && Marshal.GetLastPInvokeError() != EEXIST) return false;
        return Stat(directory) is { IsDirectory: true, IsLink: false } status && status.Owner == uid;
    }

    private abstract record Outcome
    {
        internal sealed record Done(PlatformResult<PlatformUnit> Value) : Outcome;
        // The item is on another file system than this trash.
        internal sealed record CrossDevice : Outcome;
        // This trash cannot be written; another may be.
        internal sealed record Unusable : Outcome;
        internal PlatformResult<PlatformUnit> Result => this is Done done ? done.Value : Unavailable();
    }

    private static Outcome TrashInto(string trash, string item, string recordedPath, bool createTrash)
    {
        string files = Path.Combine(trash, "files"), info = Path.Combine(trash, "info");
        // $XDG_DATA_HOME itself may not exist yet.
        if (createTrash)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(trash)!); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new Outcome.Unusable(); }
        }
        foreach (string directory in createTrash ? new[] { trash, files, info } : [files, info])
            if (MakeDirectory(directory, 0x1C0) != 0 && Marshal.GetLastPInvokeError() is var error && error != EEXIST)
                return error is EACCES or EPERM or EROFS or ENOSPC or EDQUOT ? new Outcome.Unusable() : new Outcome.Done(Failure(error));

        string name = Path.GetFileName(item);
        string content = "[Trash Info]\nPath=" + Escape(recordedPath) + "\nDeletionDate="
            + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\n";
        for (int attempt = 1; attempt < 10_000; attempt++)
        {
            string trashName = UniqueName(name, attempt);
            string infoFile = Path.Combine(info, trashName + ".trashinfo");
            // Exclusive creation claims the name, as other implementations do.
            int fd = Open(infoFile, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0x180);
            if (fd < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == EEXIST) continue;
                return error is EACCES or EPERM or EROFS or ENOSPC or EDQUOT ? new Outcome.Unusable() : new Outcome.Done(Failure(error));
            }
            using (var handle = new SafeFileHandle(fd, ownsHandle: true))
            {
                try { RandomAccess.Write(handle, Encoding.UTF8.GetBytes(content), 0); }
                catch (IOException)
                {
                    handle.Dispose(); File.Delete(infoFile);
                    return new Outcome.Unusable();
                }
            }

            int renamed = RenameNoReplace(item, Path.Combine(files, trashName));
            if (renamed == 0) return new Outcome.Done(new PlatformResult<PlatformUnit>.Success(default));
            File.Delete(infoFile);
            // A stale entry in files/ without an info file: take the next name.
            if (renamed == EEXIST || renamed == ENOTEMPTY) continue;
            return renamed switch
            {
                EXDEV => new Outcome.CrossDevice(),
                // Moving a directory into itself, as when trashing the trash's own parent.
                EINVAL => new Outcome.Done(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromErrno(renamed))),
                _ => new Outcome.Done(Failure(renamed)),
            };
        }
        return new Outcome.Done(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError));
    }

    // GIO's names: "name", then "name.2" with the number before the first extension, as in "report.2.tar.gz".
    internal static string UniqueName(string name, int attempt)
    {
        if (attempt == 1) return name;
        int dot = name.IndexOf('.', 1);
        string number = "." + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return dot < 0 ? name + number : name[..dot] + number + name[dot..];
    }

    // RFC 2396 escaping of the UTF-8 path, keeping unreserved characters and "/", as GIO writes it.
    internal static string Escape(string path)
    {
        var text = new StringBuilder(path.Length);
        foreach (byte value in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)value;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/') text.Append(c);
            else text.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    // The longest mount point that contains the directory, from this process's mount table.
    internal static string? MountPoint(string directory) => MountPoint(directory, ReadMountPoints());

    internal static string? MountPoint(string directory, IEnumerable<string> mountPoints)
    {
        string? best = null;
        foreach (string mount in mountPoints)
        {
            bool contains = mount == "/" || directory == mount || directory.StartsWith(mount + "/", StringComparison.Ordinal);
            if (contains && (best is null || mount.Length > best.Length)) best = mount;
        }
        return best;
    }

    private static IEnumerable<string> ReadMountPoints()
    {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/self/mountinfo"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        return lines.Select(line => line.Split(' ')).Where(fields => fields.Length > 4).Select(fields => Unescape(fields[4]));
    }

    // mountinfo writes space, tab, newline and backslash as octal escapes, all ASCII.
    internal static string Unescape(string field)
    {
        if (!field.Contains('\\')) return field;
        var text = new StringBuilder(field.Length);
        for (int index = 0; index < field.Length; index++)
        {
            if (field[index] == '\\' && index + 3 < field.Length
                && field.AsSpan(index + 1, 3) is var digits && digits[0] is >= '0' and <= '1' && digits[1] is >= '0' and <= '7' && digits[2] is >= '0' and <= '7')
            {
                text.Append((char)((digits[0] - '0') * 64 + (digits[1] - '0') * 8 + (digits[2] - '0')));
                index += 3;
            }
            else text.Append(field[index]);
        }
        return text.ToString();
    }

    private async ValueTask<PlatformResult<PlatformUnit>> PortalAsync(string path, CancellationToken cancellationToken)
    {
        // The portal trashes what the application can open for writing, as GIO asks it: a file opened read-write,
        // never following a link, and a directory, which cannot be opened so, by an O_PATH descriptor. The portal
        // follows an O_PATH descriptor's link and refuses one opened O_NOFOLLOW, so a symbolic link cannot be trashed.
        int fd = Open(path, O_RDWR | O_NOFOLLOW | O_CLOEXEC | O_NOCTTY | O_NONBLOCK, 0);
        if (fd < 0 && Marshal.GetLastPInvokeError() == EISDIR) fd = Open(path, O_PATH | O_CLOEXEC, 0);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ELOOP ? Unavailable() : Failure(error);
        }
        using var file = new SafeFileHandle(fd, ownsHandle: true);
        PortalConnection? session = null;
        try
        {
            session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.OwnerChanged);
            deadline.CancelAfter(PortalTimeout);
            uint result = await new Protocol.Trash(session.Connection, session.Destination, "/org/freedesktop/portal/desktop")
                .TrashFileAsync(new PortalTransport.BorrowedHandle(file)).WaitAsync(deadline.Token).ConfigureAwait(false);
            // The portal reports 0 when it cannot trash the item, including when the sandbox may not write it.
            return result == 1 ? new PlatformResult<PlatformUnit>.Success(default)
                : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError, new PlatformDiagnostic("portal", result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DBusErrorReplyException error) when (error.ErrorName == "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, new PlatformDiagnostic(error.ErrorName, 0)); }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or OperationCanceledException or NativeBackendUnavailableException)
        {
            application?.Diagnose("trash-portal-unavailable", "The Trash portal could not move the item to the trash.",
                "Ensure the session D-Bus service and xdg-desktop-portal are running; the Trash portal serves Flatpak and Snap applications.");
            return Unavailable();
        }
        finally { session?.Dispose(); }
    }

    private static PlatformResult<PlatformUnit> Unavailable() => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private static PlatformResult<PlatformUnit> Failure(int errno) => new PlatformResult<PlatformUnit>.Failed(errno switch
    {
        ENOENT or ENOTDIR => PlatformFailureCode.NotFound,
        EACCES or EPERM or EROFS => PlatformFailureCode.PermissionDenied,
        EBUSY or ETXTBSY => PlatformFailureCode.ResourceBusy,
        _ => PlatformFailureCode.IoError,
    }, PlatformDiagnostic.FromErrno(errno));

    private const int EPERM = 1, ENOENT = 2, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18, ENOTDIR = 20, EISDIR = 21, EINVAL = 22,
        ENOSPC = 28, EROFS = 30, ENOSYS = 38, ENOTEMPTY = 39, ELOOP = 40, EDQUOT = 122, ETXTBSY = 26;
    private const int O_WRONLY = 0x1, O_RDWR = 0x2, O_CREAT = 0x40, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, O_EXCL = 0x80, O_CLOEXEC = 0x80000, O_PATH = 0x200000;
    // The one flag used here whose value differs between architectures: ARM's is 0100000, x86's and others' 0400000.
    private static readonly int O_NOFOLLOW = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;
    private const int AT_FDCWD = -100;

    // renameat2(RENAME_NOREPLACE) never replaces an entry. File systems without it, and C libraries without the
    // wrapper, get a check followed by rename(2), which could replace an empty directory created in between.
    private static int RenameNoReplace(string source, string target)
    {
        try
        {
            if (RenameAt2(AT_FDCWD, source, AT_FDCWD, target, 1) == 0) return 0;
            int error = Marshal.GetLastPInvokeError();
            if (error is not (EINVAL or ENOSYS)) return error;
        }
        catch (EntryPointNotFoundException) { }
        if (Stat(target) is not null) return EEXIST;
        return Rename(source, target) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    private static string? RealPath(string path, out int errno)
    {
        nint resolved = ResolvePath(path, 0);
        if (resolved == 0) { errno = Marshal.GetLastPInvokeError(); return null; }
        try { errno = 0; return Marshal.PtrToStringUTF8(resolved); }
        finally { Free(resolved); }
    }

    internal readonly record struct Status(bool IsDirectory, bool IsLink, bool Sticky, uint Owner);

    // statx has one layout on every architecture: stx_uid at 20 and stx_mode at 28. The entry is not followed.
    private static unsafe Status? Stat(string path)
    {
        byte* buffer = stackalloc byte[256];
        try
        {
            if (StatX(AT_FDCWD, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x1 | 0x2 | 0x8 /* TYPE, MODE, UID */, buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException) { return null; }
        uint mode = *(ushort*)(buffer + 28);
        return new Status((mode & 0xF000) == 0x4000, (mode & 0xF000) == 0xA000, (mode & 0x200) != 0, *(uint*)(buffer + 20));
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags, int mode);
    [LibraryImport("libc", EntryPoint = "mkdir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MakeDirectory(string path, int mode);
    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(int sourceDirectory, string source, int targetDirectory, string target, uint flags);
    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Rename(string source, string target);
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ResolvePath(string path, nint resolved);
    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
