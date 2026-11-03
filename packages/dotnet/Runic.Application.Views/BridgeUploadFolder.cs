using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// blobs/1: the folder of one Window's uploads.
//
// - Each Window gets a new directory with a random name below a base that no other user can change. On Linux and macOS
//   it is created like mkdtemp(3), atomically with mode 0700, failing when the name exists, and then checked without
//   following links (lstat): a directory, owned by this effective user, mode 0700. On Windows it gets an explicit ACL
//   that grants only this user, SYSTEM and Administrators, without inherited entries.
// - A base is the temporary directory when it is safe, otherwise a private folder of the user's cache. A base is safe
//   when, without following links, it is a directory owned by this effective user or by root that no other user can
//   write to, or that has the sticky bit, as /tmp does, so another user can neither rename nor replace the Window's
//   directory. There is no shared fixed path, so one user's folder never affects another's uploads.
// - The sweep: when a base is first used in the process, every runic-uploads-* directory in it that this user can
//   delete and in which nothing changed for a day is deleted, so the folders of runs that crashed do not accumulate.
//   That includes the folder of another Runic application of this user that is still running but received no upload
//   for a day; such a Window creates a new folder at its next upload. Files still open are not detected (it would need
//   a scan of every process's descriptors); deleting them is harmless on Linux and macOS, and fails on Windows, where
//   the folder then stays. Links are never followed or deleted through, and another user's folders cannot be deleted.
internal static partial class BridgeUploadFolder
{
    internal const string Prefix = "runic-uploads-";
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const uint PermissionBits = 0xFFF;
    private static readonly HashSet<string> Swept = new(StringComparer.Ordinal);
    // Tests make their own directories appear to belong to another user.
    private static readonly AsyncLocal<uint?> EffectiveUserOverride = new();

    // Creates a Window's folder below the first safe base. Throws IOException when none is safe.
    internal static string Create(string? temporaryBase, string? fallbackBase, ILogger logger)
    {
        foreach (var candidate in Bases(temporaryBase, fallbackBase))
        {
            if (candidate is null) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!IsSafeBase(root)) continue;
            SweepOnce(root, logger);
            if (CreatePrivate(root) is { } directory) return directory;
        }
        throw new IOException("No upload folder can be created where only this user can write.");
    }

    private static IEnumerable<string?> Bases(string? temporaryBase, string? fallbackBase)
    {
        yield return temporaryBase ?? Path.GetTempPath();
        if (OperatingSystem.IsWindows()) yield break;
        // The private fallback, created with mode 0700 when it does not exist.
        var fallback = fallbackBase ?? DefaultFallback();
        if (fallback is null) yield break;
        try
        {
            if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback, Private);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        yield return fallback;
    }

    private static string? DefaultFallback()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathFullyQualified(cache))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            cache = Path.Combine(home, ".cache");
        }
        return Path.Combine(cache, "runic", "uploads");
    }

    // A new directory below root that only this user can use, or null when one could not be created there.
    private static string? CreatePrivate(string root)
    {
        if (OperatingSystem.IsWindows()) return CreateWindows(root);
        // mkdtemp: the name's last six characters are random, the directory is created with mode 0700, and the call
        // fails instead of using a name that exists, also a link.
        var template = System.Text.Encoding.UTF8.GetBytes(Path.Combine(root, Prefix + "XXXXXX") + "\0");
        if (MakeTemporaryDirectory(template) == 0) return null;
        var directory = System.Text.Encoding.UTF8.GetString(template, 0, template.Length - 1);
        return IsPrivateDirectory(directory) ? directory : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? CreateWindows(string root)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var directory = Path.Combine(root, Prefix + RunicUploadIds.Create());
            if (Directory.Exists(directory) || File.Exists(directory)) continue;
            new DirectoryInfo(directory).Create(PrivateSecurity());
            if (IsPrivateDirectory(directory)) return directory;
        }
        return null;
    }

    // Only this user, SYSTEM and Administrators, with full control of the folder and what it contains; nothing
    // inherited from the base.
    [SupportedOSPlatform("windows")]
    internal static DirectorySecurity PrivateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        foreach (var identity in new IdentityReference[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    // A directory, not a link, that no other user can change entries of: owned by this effective user or root, and
    // not writable by others unless sticky.
    internal static bool IsSafeBase(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        if (LinkStatus(path) is not { IsDirectory: true } status) return false;
        if (status.Owner != EffectiveUser && status.Owner != 0) return false;
        // 0x12 is group and other write (022), 0x200 the sticky bit (01000).
        var othersWrite = (status.Mode & 0x12) != 0;
        return !othersWrite || (status.Mode & 0x200) != 0;
    }

    // A Window's folder as it was created: a directory, not a link, owned by this user. On Linux and macOS mode 0700; on
    // Windows with the protected ACL of PrivateSecurity.
    internal static bool IsPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return IsPrivateWindowsDirectory(path);
        return LinkStatus(path) is { IsDirectory: true } status && status.Owner == EffectiveUser
            && (status.Mode & PermissionBits) == 0x1C0; // 0700
    }

    [SupportedOSPlatform("windows")]
    private static bool IsPrivateWindowsDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var security = info.GetAccessControl();
        return security.AreAccessRulesProtected
            && security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == WindowsIdentity.GetCurrent().User;
    }

    // A real file, not a symbolic link or another reparse point.
    internal static bool IsPlainFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void SweepOnce(string root, ILogger logger)
    {
        lock (Swept)
            if (!Swept.Add(root)) return;
        Sweep(root, DateTime.UtcNow, logger);
    }

    // Deletes the runic-uploads-* directories of root that this user can delete and in which nothing changed since
    // StaleAfter before now. A link of that name is left as it is, and so is what it points to.
    internal static void Sweep(string root, DateTime now, ILogger logger)
    {
        try
        {
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories(Prefix + "*"))
            {
                if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    // Uploads are regular files: a link inside counts for nothing, and its target's time is never read.
                    var newest = directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                        .Where(static entry => entry.LinkTarget is null)
                        .Select(static entry => entry.LastWriteTimeUtc).Append(directory.LastWriteTimeUtc).Max();
                    if (now - newest < StaleAfter) continue;
                    // Recursive deletion removes links inside the folder, never their targets.
                    directory.Delete(recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Another user's folder, or one whose files are still open on Windows.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BlobsLog.CleanupFailed(logger, error, error.GetType().Name);
        }
    }

    // ---- lstat on Linux and macOS ----

    internal readonly record struct Status(bool IsDirectory, uint Owner, uint Mode);

    // Tests: run with another effective user, so this user's directories appear foreign.
    internal static IDisposable ActAsUser(uint user)
    {
        var previous = EffectiveUserOverride.Value;
        EffectiveUserOverride.Value = user;
        return new Reset(previous);
    }

    private sealed class Reset(uint? previous) : IDisposable
    {
        public void Dispose() => EffectiveUserOverride.Value = previous;
    }

    internal static uint EffectiveUser => EffectiveUserOverride.Value ?? GetEffectiveUser();

    // The owner, type and mode of path itself, not of what a link points to; null when it cannot be read, which every
    // check counts as unsafe.
    internal static unsafe Status? LinkStatus(string path)
    {
        var buffer = stackalloc byte[512];
        if (OperatingSystem.IsLinux())
        {
            // statx's struct is the same on every architecture: stx_uid at 20, stx_mode (16 bits) at 28.
            int result;
            try { result = StatX(-100, path, 0x100, 0x1 | 0x2 | 0x8, buffer); }
            catch (EntryPointNotFoundException) { return LinuxLinkStatus(path, buffer); }
            if (result != 0) return null;
            var span = new ReadOnlySpan<byte>(buffer, 32);
            return FromMode(BinaryPrimitives.ReadUInt16LittleEndian(span[28..]), BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        }
        if (OperatingSystem.IsMacOS())
        {
            // struct stat with 64-bit inodes, the only layout of arm64 macOS: st_mode (16 bits) at 4, st_uid at 16.
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LinkStatInode64(path, buffer) : LinkStat(path, buffer);
            if (result != 0) return null;
            var span = new ReadOnlySpan<byte>(buffer, 24);
            return FromMode(BinaryPrimitives.ReadUInt16LittleEndian(span[4..]), BinaryPrimitives.ReadUInt32LittleEndian(span[16..]));
        }
        return null;
    }

    // A C library without statx (musl before 1.2.5): lstat, whose struct puts st_mode and st_uid at 24 and 28 on x64
    // and at 16 and 24 on arm64.
    private static unsafe Status? LinuxLinkStatus(string path, byte* buffer)
    {
        int result;
        try { result = LinkStat(path, buffer); }
        catch (EntryPointNotFoundException) { return null; }
        if (result != 0) return null;
        var span = new ReadOnlySpan<byte>(buffer, 32);
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => FromMode(BinaryPrimitives.ReadUInt32LittleEndian(span[24..]), BinaryPrimitives.ReadUInt32LittleEndian(span[28..])),
            Architecture.Arm64 => FromMode(BinaryPrimitives.ReadUInt32LittleEndian(span[16..]), BinaryPrimitives.ReadUInt32LittleEndian(span[24..])),
            _ => null,
        };
    }

    private static Status FromMode(uint mode, uint owner) => new((mode & 0xF000) == 0x4000, owner, mode & PermissionBits);

    private static uint GetEffectiveUser() => OperatingSystem.IsWindows() ? uint.MaxValue : GetEffectiveUserId();

    private static unsafe nint MakeTemporaryDirectory(byte[] template)
    {
        fixed (byte* pointer = template) return MakeTemporaryDirectory(pointer);
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LinkStat(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LinkStatInode64(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "mkdtemp")]
    private static unsafe partial nint MakeTemporaryDirectory(byte* template);
}
