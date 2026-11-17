/// <summary>Writes a generated file in one rename, and only when its content changed.</summary>
internal static class GeneratedFile
{
    /// <summary>The waits before each retry of a rename that Windows refused (WIN-09): about 0.6 s in all.</summary>
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(160),
        TimeSpan.FromMilliseconds(320),
    ];

    internal static void WriteIfChanged(string path, string content)
    {
        // Generated sources are shared between Windows and Unix checkouts.
        content = content.ReplaceLineEndings("\n");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath) && File.ReadAllText(fullPath) == content) return;
        // Frontend watchers (Vite, tsc --watch) must never observe a truncated
        // module, so replace the file in one rename.
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            Replace(temporary, fullPath, static (source, destination) => File.Move(source, destination, overwrite: true),
                Thread.Sleep, OperatingSystem.IsWindows());
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Moves <paramref name="temporary"/> over <paramref name="destination"/>. On Windows, a rename over a file that
    /// another process holds open without delete sharing (an editor, an antivirus scan, an indexer) fails with a sharing
    /// violation or access denied until that process lets go, so a refused rename is retried after each of
    /// <see cref="RetryDelays"/> before the error is reported. Elsewhere such a rename succeeds, and an access error
    /// is a real permission problem, so nothing is retried.
    /// </summary>
    internal static void Replace(string temporary, string destination, Action<string, string> move, Action<TimeSpan> wait, bool windows)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(wait);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                move(temporary, destination);
                return;
            }
            catch (Exception error) when (windows && attempt < RetryDelays.Length && IsTransientOnWindows(error))
            {
                wait(RetryDelays[attempt]);
            }
        }
    }

    // ERROR_ACCESS_DENIED (a pending delete or an open handle without FILE_SHARE_DELETE), ERROR_SHARING_VIOLATION and
    // ERROR_LOCK_VIOLATION, as HRESULTs.
    private static bool IsTransientOnWindows(Exception error) =>
        error is UnauthorizedAccessException or IOException &&
        error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
