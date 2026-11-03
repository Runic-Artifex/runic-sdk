using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// blobs/1: the folder of one Window's uploads. Each Window gets a new directory with a random name and mode 0700 below
// a base that no other user can change: the temporary directory when it is safe, otherwise a private folder of the
// user's cache. A base is safe when it is a real directory (not a symbolic link) that only its owner can write, or that
// has the sticky bit, as /tmp does, so another user can neither rename nor replace the Window's directory. There is no
// shared fixed path, so one user's folder never affects another's uploads.
//
// Folders of this process's earlier runs that crashed are swept from the base when it is first used: a directory named
// runic-uploads-* that nothing changed for a day. Another user's folders cannot be deleted and stay.
internal static class BridgeUploadFolder
{
    internal const string Prefix = "runic-uploads-";
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly HashSet<string> Swept = new(StringComparer.Ordinal);

    // Creates a Window's folder below the first safe base. Throws IOException when none is safe.
    internal static string Create(string? temporaryBase, string? fallbackBase, ILogger logger)
    {
        foreach (var candidate in Bases(temporaryBase, fallbackBase))
        {
            if (candidate is null) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!IsSafeBase(root)) continue;
            Sweep(root, logger);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var directory = Path.Combine(root, Prefix + RunicUploadIds.Create());
                if (Directory.Exists(directory) || File.Exists(directory)) continue;
                var created = OperatingSystem.IsWindows() ? Directory.CreateDirectory(directory) : Directory.CreateDirectory(directory, Private);
                if (!OperatingSystem.IsWindows() && created.UnixFileMode != Private) File.SetUnixFileMode(directory, Private);
                if (IsPrivateDirectory(directory)) return directory;
            }
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

    // A real directory that no other user can change entries of.
    internal static bool IsSafeBase(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        if (OperatingSystem.IsWindows()) return true;
        var mode = info.UnixFileMode;
        var othersWrite = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
        return !othersWrite || (mode & UnixFileMode.StickyBit) != 0;
    }

    // A Window's folder as it was created: a real directory, mode 0700.
    internal static bool IsPrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        return OperatingSystem.IsWindows() || info.UnixFileMode == Private;
    }

    // A real file, not a symbolic link or another reparse point.
    internal static bool IsPlainFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    // Deletes the folders of crashed runs, once per base and process.
    private static void Sweep(string root, ILogger logger)
    {
        lock (Swept)
            if (!Swept.Add(root)) return;
        try
        {
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories(Prefix + "*"))
            {
                if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    var newest = directory.EnumerateFileSystemInfos().Select(static entry => entry.LastWriteTimeUtc)
                        .Append(directory.LastWriteTimeUtc).Max();
                    if (DateTime.UtcNow - newest < StaleAfter) continue;
                    directory.Delete(recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Another user's folder, or one in use.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BlobsLog.CleanupFailed(logger, error, error.GetType().Name);
        }
    }
}
