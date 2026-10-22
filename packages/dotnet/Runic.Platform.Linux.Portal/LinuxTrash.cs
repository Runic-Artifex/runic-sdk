using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The freedesktop.org Trash specification 1.0. Outside a sandbox the provider implements it itself, as GIO and KIO do:
// an item on the home trash's file system goes to $XDG_DATA_HOME/Trash; an item elsewhere goes to the trash at the
// top of its mount, $topdir/.Trash/$uid or $topdir/.Trash-$uid. The info file is created exclusively first, then the
// item is renamed into files/; a rename never falls back to copying, so nothing is deleted permanently. A trashed
// directory's size goes to the trash's directorysizes cache. Listing, restoring and emptying read the home trash and
// the topdir trashes this user owns on every mounted file system. Inside Flatpak or Snap, where the user's trash is out
// of reach, the Trash portal moves the item, and listing, restoring and emptying are unavailable.
internal sealed partial class LinuxTrash(PortalApplication? application = null, string? homeTrash = null, bool? sandboxed = null,
    string? address = null, string destination = "org.freedesktop.portal.Desktop", Func<string, string?>? mountPointOf = null,
    Func<IEnumerable<string>>? mountPoints = null) : IDesktopTrash
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

    public async ValueTask<PlatformResult<ImmutableArray<DesktopTrashItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed) return new PlatformResult<ImmutableArray<DesktopTrashItem>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return await Task.Run(List, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed) return Unavailable();
        return await Task.Run(() => Restore(itemId), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EmptyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed) return Unavailable();
        return await Task.Run(Empty, cancellationToken).ConfigureAwait(false);
    }

    // The home trash, then the topdir trashes of mounted file systems that this user owns: $topdir/.Trash/$uid under a
    // sticky .Trash that is not a link, and $topdir/.Trash-$uid. Nothing is created.
    private List<(string Trash, string? Topdir)> Trashes()
    {
        var trashes = new List<(string, string?)> { (homeTrash ?? HomeTrash(), null) };
        uint uid = GetEffectiveUserId();
        string user = uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (string topdir in (mountPoints ?? ReadMountPoints)().Distinct(StringComparer.Ordinal))
        {
            string shared = Path.Combine(topdir, ".Trash");
            if (Stat(shared) is { IsDirectory: true, IsLink: false, Sticky: true } && Path.Combine(shared, user) is var own && Owned(own, uid))
                trashes.Add((own, topdir));
            if (Path.Combine(topdir, ".Trash-" + user) is var personal && Owned(personal, uid)) trashes.Add((personal, topdir));
        }
        return trashes;

        static bool Owned(string directory, uint uid) => Stat(directory) is { IsDirectory: true, IsLink: false } status && status.Owner == uid;
    }

    private PlatformResult<ImmutableArray<DesktopTrashItem>> List()
    {
        var items = ImmutableArray.CreateBuilder<DesktopTrashItem>();
        foreach (var (trash, topdir) in Trashes())
        {
            string info = Path.Combine(trash, "info"), files = Path.Combine(trash, "files");
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(info, "*.trashinfo").ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            var sizes = DirectorySizes.Read(trash);
            foreach (string infoFile in entries)
            {
                string name = Path.GetFileNameWithoutExtension(infoFile);
                if (ReadInfo(infoFile, topdir) is not { } record) continue;
                string content = Path.Combine(files, name);
                // An info file without its item is stale, as other implementations treat it.
                if (Measure(content) is not { } item) continue;
                long? size = item.IsDirectory
                    ? sizes.TryGetValue(name, out var cached) && Measure(infoFile) is { } infoStatus && infoStatus.ModifiedSeconds == cached.Modified ? cached.Size : null
                    : item.Size;
                items.Add(new DesktopTrashItem(content, record.OriginalPath) { DeletedAt = record.DeletedAt, Size = size, IsDirectory = item.IsDirectory });
            }
        }
        return new PlatformResult<ImmutableArray<DesktopTrashItem>>.Success(items.ToImmutable());
    }

    // An item's ID is its entry in a known trash's files/ directory.
    private PlatformResult<PlatformUnit> Restore(string itemId)
    {
        string? files = Path.GetDirectoryName(itemId), name = Path.GetFileName(itemId);
        if (!Path.IsPathFullyQualified(itemId) || files is null || string.IsNullOrEmpty(name) || Path.GetFileName(files) != "files")
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
        string trash = Path.GetDirectoryName(files)!;
        if (Trashes().FirstOrDefault(known => known.Trash == trash) is not { Trash: not null } found)
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
        string infoFile = Path.Combine(trash, "info", name + ".trashinfo");
        if (ReadInfo(infoFile, found.Topdir) is not { } record || Stat(itemId) is null) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
        string target = record.OriginalPath;
        // Nothing at the original path is ever replaced.
        if (Stat(target) is not null) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict);
        if (CreateParents(Path.GetDirectoryName(target)!) is var created && created != 0) return Failure(created);
        bool directory = Stat(itemId) is { IsDirectory: true, IsLink: false };
        int renamed = RenameNoReplace(itemId, target);
        if (renamed == EEXIST || renamed == ENOTEMPTY) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict, PlatformDiagnostic.FromErrno(renamed));
        if (renamed != 0) return Failure(renamed);
        try { File.Delete(infoFile); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (directory) DirectorySizes.Remove(trash, name);
        return new PlatformResult<PlatformUnit>.Success(default);
    }

    // Each item goes before its info file, so a failure leaves an item that is still listed; links are deleted
    // themselves, never followed. The first failure is reported after every trash was emptied as far as it could be.
    private PlatformResult<PlatformUnit> Empty()
    {
        PlatformResult<PlatformUnit>? failure = null;
        foreach (var (trash, _) in Trashes())
        {
            string info = Path.Combine(trash, "info"), files = Path.Combine(trash, "files");
            foreach (string infoFile in Entries(info, "*.trashinfo"))
            {
                string content = Path.Combine(files, Path.GetFileNameWithoutExtension(infoFile));
                if (Delete(content) is { } failed) { failure ??= failed; continue; }
                if (Delete(infoFile) is { } infoFailed) failure ??= infoFailed;
            }
            // Entries without an info file are left over from an interrupted trashing; they go too.
            foreach (string content in Entries(files, "*")) if (Delete(content) is { } failed) failure ??= failed;
            if (Delete(Path.Combine(trash, "directorysizes")) is { } sizesFailed) failure ??= sizesFailed;
        }
        return failure ?? new PlatformResult<PlatformUnit>.Success(default);

        static IEnumerable<string> Entries(string directory, string pattern)
        {
            try { return Directory.EnumerateFileSystemEntries(directory, pattern).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    // Deletes an entry without following it; a missing one is already gone.
    private static PlatformResult<PlatformUnit>? Delete(string path)
    {
        try
        {
            switch (Stat(path))
            {
                case null: return null;
                case { IsDirectory: true, IsLink: false }: Directory.Delete(path, recursive: true); break;
                default: File.Delete(path); break;
            }
            return null;
        }
        catch (UnauthorizedAccessException error) { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, Diagnose(error)); }
        catch (IOException error) { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, Diagnose(error)); }
    }

    private static PlatformDiagnostic? Diagnose(Exception error) => error.HResult is > 0 and < 4096 ? PlatformDiagnostic.FromErrno(error.HResult) : null;

    // Creates the missing directories of a path, as mkdir -p does, with the default mode.
    private static int CreateParents(string directory)
    {
        if (Stat(directory) is { } existing) return existing.IsDirectory || Stat(directory, follow: true) is { IsDirectory: true } ? 0 : ENOTDIR;
        if (Path.GetDirectoryName(directory) is { } parent && parent != directory && CreateParents(parent) is var error && error != 0) return error;
        return MakeDirectory(directory, 0x1FF) == 0 || Marshal.GetLastPInvokeError() == EEXIST ? 0 : Marshal.GetLastPInvokeError();
    }

    internal readonly record struct TrashInfo(string OriginalPath, DateTimeOffset? DeletedAt);

    private static TrashInfo? ReadInfo(string infoFile, string? topdir)
    {
        string text;
        try { text = File.ReadAllText(infoFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return ParseInfo(text, topdir);
    }

    // The [Trash Info] group: Path, percent-encoded and relative to the top directory in a topdir trash, and
    // DeletionDate in local time. An absolute path in a topdir trash is allowed, a relative one in the home trash not.
    internal static TrashInfo? ParseInfo(string text, string? topdir)
    {
        string? path = null, date = null;
        bool group = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[') { if (group) break; group = line == "[Trash Info]"; if (!group) return null; continue; }
            if (!group) return null;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string key = line[..equals].TrimEnd(), value = line[(equals + 1)..].TrimStart();
            if (key == "Path") path ??= value;
            else if (key == "DeletionDate") date ??= value;
        }
        if (path is null || DecodePath(path) is not { Length: > 0 } decoded) return null;
        if (decoded[0] != '/')
        {
            if (topdir is null) return null;
            decoded = Path.Combine(topdir, decoded);
        }
        DateTimeOffset? deleted = DateTime.TryParseExact(date, "yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var local) ? new DateTimeOffset(local) : null;
        return new TrashInfo(Path.TrimEndingDirectorySeparator(decoded), deleted);
    }

    // Percent-decoding of the UTF-8 path; null for a broken escape, invalid UTF-8 or NUL.
    internal static string? DecodePath(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte decoded)) return null;
                bytes.Add(decoded);
                index += 2;
            }
            else if (value[index] < 0x80) bytes.Add((byte)value[index]);
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[index].ToString()));
        }
        try
        {
            string path = new UTF8Encoding(false, true).GetString([.. bytes]);
            return path.Contains('\0') ? null : path;
        }
        catch (DecoderFallbackException) { return null; }
    }

    // The trash's directorysizes cache (specification 1.0): "size mtime name" per trashed directory, with the size in
    // bytes of the blocks it and its contents use, the info file's modification time and the percent-encoded name.
    // It is rewritten through a temporary file and a rename, as the specification asks, so readers never see half.
    internal static class DirectorySizes
    {
        internal static Dictionary<string, (long Size, long Modified)> Read(string trash)
        {
            var sizes = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            string[] lines;
            try { lines = File.ReadAllLines(Path.Combine(trash, "directorysizes")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return sizes; }
            foreach (string line in lines)
                if (Parse(line) is { } entry) sizes[entry.Name] = (entry.Size, entry.Modified);
            return sizes;
        }

        internal static (long Size, long Modified, string Name)? Parse(string line)
        {
            string[] fields = line.Split(' ');
            if (fields.Length != 3 || !long.TryParse(fields[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long size)
                || !long.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long modified)
                || DecodePath(fields[2]) is not { Length: > 0 } name || name.Contains('/')) return null;
            return (size, modified, name);
        }

        internal static string Line(long size, long modified, string name) =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size} {modified} {Escape(name)}");

        // After a directory was trashed: its size and its info file's time. A failure leaves the cache as it was; the
        // specification makes it optional.
        internal static void Add(string trash, string name)
        {
            string content = Path.Combine(trash, "files", name), info = Path.Combine(trash, "info", name + ".trashinfo");
            if (Measure(info) is not { } infoStatus || Blocks(content) is not { } size) return;
            Rewrite(trash, lines => [.. lines.Where(line => Parse(line)?.Name != name), Line(size, infoStatus.ModifiedSeconds, name)]);
        }

        internal static void Remove(string trash, string name)
        {
            if (File.Exists(Path.Combine(trash, "directorysizes"))) Rewrite(trash, lines => [.. lines.Where(line => Parse(line)?.Name != name)]);
        }

        private static void Rewrite(string trash, Func<string[], string[]> change)
        {
            string target = Path.Combine(trash, "directorysizes");
            string temporary = Path.Combine(trash, "directorysizes." + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string[] lines = File.Exists(target) ? File.ReadAllLines(target) : [];
                File.WriteAllLines(temporary, change(lines));
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }

        // The bytes of the blocks a directory and everything below it use, as du -B1 counts them; links are not followed.
        internal static long? Blocks(string directory)
        {
            if (Measure(directory) is not { IsDirectory: true } root) return null;
            long total = root.Blocks * 512;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
                    if (Measure(entry) is { } status) total += status.Blocks * 512;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            return total;
        }
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
            if (renamed == 0)
            {
                if (Stat(Path.Combine(files, trashName)) is { IsDirectory: true, IsLink: false }) DirectorySizes.Add(trash, trashName);
                return new Outcome.Done(new PlatformResult<PlatformUnit>.Success(default));
            }
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
        return new Outcome.Done(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other));
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
        // never following a link, and a directory, which cannot be opened so, by an O_PATH descriptor. A symbolic link
        // goes as an O_PATH | O_NOFOLLOW descriptor of the link itself: xdg-desktop-portal 1.22 trashes such a
        // descriptor without following it; an older portal refuses it, which reports the link unavailable.
        bool link = false;
        int fd = Open(path, O_RDWR | O_NOFOLLOW | O_CLOEXEC | O_NOCTTY | O_NONBLOCK, 0);
        if (fd < 0 && Marshal.GetLastPInvokeError() == EISDIR) fd = Open(path, O_PATH | O_CLOEXEC, 0);
        else if (fd < 0 && Marshal.GetLastPInvokeError() == ELOOP)
        {
            link = true;
            fd = Open(path, O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
        }
        if (fd < 0) return Failure(Marshal.GetLastPInvokeError());
        using var file = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.OwnerChanged);
            deadline.CancelAfter(PortalTimeout);
            // The connection sends a duplicate of the descriptor; this method closes its own.
            uint result = await new Protocol.Trash(session.Connection, session.Destination, "/org/freedesktop/portal/desktop")
                .TrashFileAsync(file, cancellationToken: deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            // The portal reports 0 when it cannot trash the item, including when the sandbox may not write it; for a link,
            // that is a portal that cannot trash links.
            return result == 1 ? new PlatformResult<PlatformUnit>.Success(default)
                : link ? Unavailable()
                : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, new PlatformDiagnostic("portal", result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, new PlatformDiagnostic(error.ErrorName, 0)); }
        catch (Exception error) when (error is DBusException or TimeoutException or OperationCanceledException or NativeBackendUnavailableException)
        {
            application?.Diagnose("trash-portal-unavailable", "The Trash portal could not move the item to the trash.",
                "Ensure the session D-Bus service and xdg-desktop-portal are running; the Trash portal serves Flatpak and Snap applications.");
            return Unavailable();
        }
    }

    private static PlatformResult<PlatformUnit> Unavailable() => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private static PlatformResult<PlatformUnit> Failure(int errno) => new PlatformResult<PlatformUnit>.Failed(errno switch
    {
        ENOENT or ENOTDIR => PlatformFailureCode.NotFound,
        EACCES or EPERM or EROFS => PlatformFailureCode.PermissionDenied,
        EBUSY or ETXTBSY => PlatformFailureCode.ResourceBusy,
        EIO or ENOSPC or EDQUOT => PlatformFailureCode.IOError,
        _ => PlatformFailureCode.Other,
    }, PlatformDiagnostic.FromErrno(errno));

    private const int EPERM = 1, ENOENT = 2, EIO = 5, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18, ENOTDIR = 20, EISDIR = 21, EINVAL = 22,
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

    // statx has one layout on every architecture: stx_uid at 20 and stx_mode at 28. The entry is not followed unless asked.
    private static unsafe Status? Stat(string path, bool follow = false)
    {
        byte* buffer = stackalloc byte[256];
        try
        {
            if (StatX(AT_FDCWD, path, follow ? 0 : 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x1 | 0x2 | 0x8 /* TYPE, MODE, UID */, buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException) { return null; }
        uint mode = *(ushort*)(buffer + 28);
        return new Status((mode & 0xF000) == 0x4000, (mode & 0xF000) == 0xA000, (mode & 0x200) != 0, *(uint*)(buffer + 20));
    }

    internal readonly record struct Measured(bool IsDirectory, long Size, long Blocks, long ModifiedSeconds);

    // An entry's type, size, blocks and modification time, not following a link: stx_size at 40, stx_blocks at 48 and
    // stx_mtime's seconds at 112.
    private static unsafe Measured? Measure(string path)
    {
        byte* buffer = stackalloc byte[256];
        try
        {
            if (StatX(AT_FDCWD, path, 0x100, 0x1 | 0x2 | 0x40 | 0x200 | 0x400 /* TYPE, MODE, MTIME, SIZE, BLOCKS */, buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException) { return null; }
        uint mode = *(ushort*)(buffer + 28);
        return new Measured((mode & 0xF000) == 0x4000, (long)*(ulong*)(buffer + 40), (long)*(ulong*)(buffer + 48), *(long*)(buffer + 112));
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
