// Shared by the test and smoke projects that set RunicTestTimeouts=true (eng/build/test-timeouts.targets).
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stops a process a test started, with its descendants, and deletes the directories it used.
/// </summary>
/// <remarks>
/// <para>
/// A process that counts as exited has not always let go of its files. Waiting for the root process does not wait for
/// its descendants, which can still write into the test's directory (#1273). On Windows, a process ended through a job
/// object, or by <see cref="Process.Kill(bool)"/>, closes its working-directory handle a fraction of a millisecond after
/// its handle is signalled, and no process or job state reports the release (#1268); a delete in that window fails
/// with a sharing violation.
/// </para>
/// <para>
/// <see cref="StopTreeAsync"/> therefore ends the tree and waits for the root and, on Linux, for every descendant seen
/// before the kill. <see cref="DeleteDirectory"/> deletes on Windows with a bounded retry on <see cref="IOException"/> and
/// <see cref="UnauthorizedAccessException"/>; the limit is scaled by <c>RUNIC_TEST_TIMEOUT_SCALE</c>, and a delete that
/// still fails names the files left. Elsewhere a failure is not retried: POSIX deletes open files, and a failure there
/// is a real one.
/// </para>
/// </remarks>
internal static class TestProcesses
{
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(10);

    /// <summary>Ends <paramref name="process"/> and its descendants, and waits until they exited.</summary>
    /// <param name="process">The root, started by the test. Nothing happens when it has exited and has no descendants.</param>
    /// <param name="limit">The unscaled wait for the exits; 10 seconds by default.</param>
    public static async Task StopTreeAsync(Process process, TimeSpan? limit = null)
    {
        var deadline = Stopwatch.StartNew();
        var wait = TestTimeouts.Scale(limit ?? DefaultLimit);
        var descendants = HasExited(process) ? [] : Descendants(process.Id);
        try
        {
            // macOS's recursive kill can hang (dotnet/runtime#131944); its children observe their parent's exit.
            if (!HasExited(process)) process.Kill(entireProcessTree: !OperatingSystem.IsMacOS());
        }
        catch (InvalidOperationException)
        {
            // It exited meanwhile.
        }
        await process.WaitForExitAsync().WaitAsync(Remaining(wait, deadline)).ConfigureAwait(false);
        foreach (var (id, started) in descendants)
        {
            while (IsRunning(id, started))
            {
                if (deadline.Elapsed >= wait)
                    throw new TimeoutException($"Process {id}, a descendant of {process.Id}, did not exit within {wait.TotalSeconds:0.#} s of the stop.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deletes <paramref name="path"/> and everything in it; nothing happens when it does not exist.</summary>
    /// <param name="path">A directory the test owns.</param>
    /// <param name="limit">The unscaled time Windows has to release a stopped process's handles; 10 seconds by default.</param>
    public static void DeleteDirectory(string path, TimeSpan? limit = null)
    {
        var deadline = Stopwatch.StartNew();
        var wait = TestTimeouts.Scale(limit ?? DefaultLimit);
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
            {
                if (deadline.Elapsed >= wait) throw new IOException($"{path} could not be deleted within {wait.TotalSeconds:0.#} s: {exception.Message} Left: {Left(path)}.", exception);
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>The asynchronous form of <see cref="DeleteDirectory"/>.</summary>
    public static async Task DeleteDirectoryAsync(string path, TimeSpan? limit = null)
    {
        var deadline = Stopwatch.StartNew();
        var wait = TestTimeouts.Scale(limit ?? DefaultLimit);
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
            {
                if (deadline.Elapsed >= wait) throw new IOException($"{path} could not be deleted within {wait.TotalSeconds:0.#} s: {exception.Message} Left: {Left(path)}.", exception);
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    // Linux: the descendants of a process, each with its start time so a reused process id is not waited for. Windows
    // and macOS expose no parent ids without native calls; there the delete's retry covers a descendant still exiting.
    internal static IReadOnlyList<(int Id, string Started)> Descendants(int root)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var children = new Dictionary<int, List<(int Id, string Started)>>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            if (Stat(id) is not { } stat) continue;
            if (!children.TryGetValue(stat.Parent, out var list)) children[stat.Parent] = list = [];
            list.Add((id, stat.Started));
        }
        var found = new List<(int Id, string Started)>();
        var pending = new Queue<int>([root]);
        while (pending.Count > 0)
        {
            if (!children.TryGetValue(pending.Dequeue(), out var list)) continue;
            foreach (var child in list)
            {
                found.Add(child);
                pending.Enqueue(child.Id);
            }
        }
        return found;
    }

    // /proc/<id>/stat: id (comm) state ppid ... starttime (field 22). comm can hold spaces and parentheses.
    private static (int Parent, string Started, char State)? Stat(int id)
    {
        string text;
        try { text = File.ReadAllText($"/proc/{id}/stat"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        var close = text.LastIndexOf(')');
        if (close < 0) return null;
        var fields = text[(close + 2)..].Split(' ');
        if (fields.Length < 20 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)) return null;
        return (parent, fields[19], fields[0][0]);
    }

    // A zombie has exited: it holds no files, only its entry until its new parent reaps it.
    private static bool IsRunning(int id, string started) => Stat(id) is { } stat && stat.Started == started && stat.State != 'Z';

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static TimeSpan Remaining(TimeSpan wait, Stopwatch deadline) =>
        wait - deadline.Elapsed is { } remaining && remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

    private static string Left(string path)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Take(10).Select(entry => Path.GetRelativePath(path, entry)).ToList();
            return entries.Count == 0 ? "the empty directory" : string.Join(", ", entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }
}
