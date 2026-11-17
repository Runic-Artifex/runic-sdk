using System;
using System.IO;
using System.Threading;

namespace Runic.Application.Tool;

/// <summary>
/// Replaces a file whole with one rename, as dev does for the session file, the frontend marker and the frontend
/// compiler's ready snapshot (#1102). The codegen tool has its own copy of this policy (WIN-09, <c>GeneratedFile</c>).
/// </summary>
internal static class FileReplace
{
    /// <summary>The waits before each retry of a rename that Windows refused: about 0.6 s in all.</summary>
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(160),
        TimeSpan.FromMilliseconds(320),
    ];

    /// <summary>Moves <paramref name="temporary"/> over <paramref name="destination"/>, retrying as <see cref="Move(string, string, Action{string, string}, Action{TimeSpan}, bool)"/> describes.</summary>
    internal static void Move(string temporary, string destination) =>
        Move(temporary, destination, static (source, target) => File.Move(source, target, overwrite: true),
            Thread.Sleep, OperatingSystem.IsWindows());

    /// <summary>
    /// Moves <paramref name="temporary"/> over <paramref name="destination"/>. On Windows, a rename over a file that
    /// another process holds open without delete sharing (an IDE or project system reading <c>obj/</c>, an antivirus
    /// scan, an indexer) fails with access denied or a sharing violation until that process lets go, so a refused
    /// rename is retried after each of <see cref="RetryDelays"/> before the error is reported. Elsewhere such a rename
    /// succeeds, and an access error is a real permission problem, so nothing is retried.
    /// </summary>
    internal static void Move(string temporary, string destination, Action<string, string> move, Action<TimeSpan> wait, bool windows)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(wait);
        for (int attempt = 0; ; attempt++)
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
    internal static bool IsTransientOnWindows(Exception error) =>
        error is UnauthorizedAccessException or IOException &&
        error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);
}

/// <summary>
/// A session file or frontend marker that dev could not replace, even after <see cref="FileReplace"/>'s retries. dev
/// keeps the state it meant to write and writes it again on its next change or heartbeat.
/// </summary>
internal sealed class DevSessionFileException(string path, Exception inner)
    : IOException($"Could not replace {path}: {inner.Message}", inner)
{
    internal string Path { get; } = path;
}
