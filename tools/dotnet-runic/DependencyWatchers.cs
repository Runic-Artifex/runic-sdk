using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>What dev knows about a dependency package now, as the session file's <c>dependencies[]</c> lists it.</summary>
/// <param name="State">
/// <c>built</c>, <c>building</c>, <c>failed</c> (its last rebuild failed; the page keeps the last good build),
/// <c>missing-output</c>, <c>source</c> (Vite serves its sources) or <c>not-built</c> (its build is off).
/// </param>
/// <param name="Watch">
/// <c>script</c> (its <c>watch</c> script runs), <c>rebuild</c> (dev reruns its build on a change), <c>vite</c> (a source
/// package that Vite watches) or <c>none</c>.
/// </param>
/// <param name="Message">The last failure, or null.</param>
internal sealed record DependencyState(string State, string Watch, string? Message = null)
{
    internal const string Built = "built";
    internal const string Building = "building";
    internal const string Failed = "failed";
    internal const string MissingOutput = "missing-output";
    internal const string Source = "source";
    internal const string NotBuilt = "not-built";

    /// <summary>The state before dev watches anything: from the package's kind and its exports on disk.</summary>
    internal static DependencyState Initial(FrontendDependency dependency, bool watching)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (dependency.Kind != "compiled") return new(Source, watching ? "vite" : "none");
        string state = dependency.MissingExport() is null ? Built : MissingOutput;
        if (!dependency.IsBuilt) return new(state == Built ? NotBuilt : MissingOutput, "none");
        return new(state, !watching ? "none" : dependency.WatchCommand.Length != 0 ? "script" : "rebuild");
    }
}

/// <summary>
/// dev's watchers for the compiled frontend packages that the page depends on (devtools-library-dx §3.5, D081.6). A
/// package with a <c>watch</c> script runs it; otherwise dev reruns the package's build, debounced, when a file of the
/// package changes outside <c>node_modules</c>, <c>bin</c>, <c>obj</c>, hidden folders and its output folders. Vite sees the
/// new output and updates the page. A rebuild that fails is reported as a <c>RAPPDEV1018</c> line, and dev puts the
/// output folders of the last good build back, so the page keeps working; a watch script that exits is reported the
/// same way, and dev rebuilds the package itself from then on. Source packages need nothing: Vite watches them.
/// </summary>
internal sealed class DependencyWatchers : IAsyncDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource _stop;
    private readonly List<Task> _watches = [];
    private readonly Action<FrontendDependency, DependencyState> _changed;
    private readonly string _stateDirectory;

    private DependencyWatchers(CancellationTokenSource stop, Action<FrontendDependency, DependencyState> changed, string stateDirectory)
    {
        _stop = stop;
        _changed = changed;
        _stateDirectory = stateDirectory;
    }

    /// <summary>Starts one watch per compiled package whose build is on; <paramref name="changed"/> hears every state change.</summary>
    internal static DependencyWatchers Start(
        DevProjectConfiguration configuration,
        Action<FrontendDependency, DependencyState> changed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(changed);
        var watchers = new DependencyWatchers(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken), changed,
            Path.Combine(configuration.SessionDirectory, "dependency-builds"));
        foreach (FrontendDependency dependency in configuration.FrontendDependencies.Where(static dependency => dependency.IsBuilt))
        {
            Console.WriteLine(dependency.WatchCommand.Length != 0
                ? $"[dev] Watching {dependency.DisplayName} with '{dependency.WatchCommand}' in {dependency.Directory}."
                : $"[dev] Watching {dependency.DisplayName}: dev reruns '{dependency.BuildCommand}' when a file of {dependency.Directory} changes.");
            watchers._watches.Add(Task.Run(() => watchers.WatchAsync(dependency, watchers._stop.Token), CancellationToken.None));
        }
        return watchers;
    }

    private async Task WatchAsync(FrontendDependency dependency, CancellationToken cancellationToken)
    {
        try
        {
            if (dependency.WatchCommand.Length != 0 &&
                await RunWatchScriptAsync(dependency, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await PollAsync(dependency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DevDevelopmentException)
        {
            // A watcher that cannot run must not end dev: the page keeps its build, and the line says why.
            Report(dependency, new(DependencyState.Failed, "none", error.Message),
                $"dev stopped watching {dependency.DisplayName}: {error.Message}");
        }
    }

    /// <summary>
    /// Runs the watch script until dev stops (true) or the script exits (false; dev polls instead). The script decides
    /// what it writes, so dev cannot keep the last good build for it (with <c>tsc -w</c>, <c>noEmitOnError</c> does): a
    /// located error in its output (<c>src/x.ts(3,1): error TS1005: …</c>) is reported as a RAPPDEV1018 line, and the
    /// package counts as built again once its outputs change and no error followed for a second.
    /// </summary>
    private async Task<bool> RunWatchScriptAsync(FrontendDependency dependency, CancellationToken cancellationToken)
    {
        (string executable, IReadOnlyList<string> arguments) = ShellCommand.Parse(dependency.WatchCommand);
        using var repeated = new RepeatedLineFilter(dependency.Label, Console.WriteLine);
        var errors = new WatchErrors(TimeProvider.System);
        await using RunningProcess process = RunningProcess.Start(dependency.Label, executable, dependency.Directory, arguments,
            suppressLine: repeated.Suppress, observeLine: errors.Observe, endTreeAtStop: true);
        _changed(dependency, new(DependencyState.Built, "script"));
        Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        string outputs = OutputFingerprint(dependency);
        bool failed = false;
        while (!process.Completion.IsCompleted)
        {
            if (await Task.WhenAny(process.Completion, cancelled, Task.Delay(PollInterval, CancellationToken.None)).ConfigureAwait(false) == cancelled) return true;
            if (!failed && errors.TakeNew() is { } error)
            {
                failed = true;
                outputs = OutputFingerprint(dependency);
                Report(dependency, new(DependencyState.Failed, "script", error),
                    $"'{dependency.WatchCommand}' of {dependency.DisplayName} reported an error: {error} Its output is above; the page shows what the script wrote.");
            }
            else if (failed && errors.QuietFor(TimeSpan.FromSeconds(1)) && OutputFingerprint(dependency) is var current && current != outputs)
            {
                failed = false;
                errors.TakeNew();
                _changed(dependency, new(DependencyState.Built, "script"));
                Console.WriteLine($"[dev] {dependency.DisplayName} builds again.");
            }
        }
        if (cancellationToken.IsCancellationRequested) return true;
        int exitCode = await process.Completion.ConfigureAwait(false);
        Report(dependency, new(DependencyState.Failed, "rebuild", $"'{dependency.WatchCommand}' exited with code {exitCode}."),
            $"'{dependency.WatchCommand}' of {dependency.DisplayName} exited with code {Code(exitCode)}; dev reruns its build " +
            $"'{dependency.BuildCommand}' when a file changes instead. Its output is above.");
        return false;
    }

    /// <summary>The fingerprint of the package's output folders (path, size, write time).</summary>
    private static string OutputFingerprint(FrontendDependency dependency)
    {
        var builder = new StringBuilder();
        foreach (string output in dependency.Outputs)
        {
            string folder = Path.GetFullPath(output, dependency.Directory);
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    builder.Append(file).Append('\0').Append(info.Length).Append('\0').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return builder.ToString();
    }

    private async Task PollAsync(FrontendDependency dependency, CancellationToken cancellationToken)
    {
        string fingerprint = Fingerprint(dependency);
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            string current = Fingerprint(dependency);
            if (current == fingerprint) continue;
            // Debounced: an editor or a git checkout writes several files; build once they stop changing.
            string settled;
            do
            {
                settled = current;
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                current = Fingerprint(dependency);
            }
            while (current != settled);
            await RebuildAsync(dependency, cancellationToken).ConfigureAwait(false);
            // After the build: what the build itself writes outside its outputs (a .tsbuildinfo) must not start another.
            fingerprint = Fingerprint(dependency);
        }
    }

    /// <summary>
    /// Reruns the package's build. Its output folders are copied aside first and put back when the build fails or does
    /// not write the package's exports, so Vite keeps serving the last good build (D081.6).
    /// </summary>
    internal async Task<bool> RebuildAsync(FrontendDependency dependency, CancellationToken cancellationToken)
    {
        _changed(dependency, new(DependencyState.Building, "rebuild"));
        string backup = Path.Combine(_stateDirectory, dependency.Label + "-" + ShortHash(dependency.Directory));
        bool backedUp = BackUp(dependency, backup);
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"[dev] {dependency.DisplayName} changed; running '{dependency.BuildCommand}'.");
        (string executable, IReadOnlyList<string> arguments) = ShellCommand.Parse(dependency.BuildCommand);
        int exitCode;
        // The build's lock of this package folder, so a build that dotnet watch's restart runs does not build it at once.
        using (FileStream? gate = await PackageBuildLock.AcquireAsync(dependency.Directory, cancellationToken).ConfigureAwait(false))
        using (var repeated = new RepeatedLineFilter(dependency.Label, Console.WriteLine, flushPeriodically: false))
        {
            await using RunningProcess process = RunningProcess.Start(dependency.Label, executable, dependency.Directory, arguments,
                suppressLine: repeated.Suppress, endTreeAtStop: true);
            Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Task.WhenAny(process.Completion, cancelled).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            exitCode = await process.Completion.ConfigureAwait(false);
            await process.DrainOutputAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        string? missing = exitCode == 0 ? dependency.MissingExport() : null;
        if (exitCode == 0 && missing is null)
        {
            TryDelete(backup);
            _changed(dependency, new(DependencyState.Built, "rebuild"));
            Console.WriteLine($"[dev] Rebuilt {dependency.DisplayName} in {timer.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s.");
            return true;
        }
        string failure = missing is null
            ? $"The build '{dependency.BuildCommand}' of {dependency.DisplayName} exited with code {Code(exitCode)}"
            : $"The build '{dependency.BuildCommand}' of {dependency.DisplayName} did not write {Path.GetRelativePath(dependency.Directory, missing).Replace('\\', '/')}, which its package.json exports";
        bool restored = backedUp && Restore(dependency, backup);
        Report(dependency, new(DependencyState.Failed, "rebuild", failure + "."),
            failure + (restored
                ? "; dev put its last good build back, so the page keeps it. Its output is above; dev rebuilds it on the next change."
                : "; the page keeps what Vite loaded last. Its output is above; dev rebuilds it on the next change."));
        return false;
    }

    private void Report(FrontendDependency dependency, DependencyState state, string message)
    {
        _changed(dependency, state);
        Console.Error.WriteLine(DevApplication.DiagnosticLine(
            dependency.Owner ?? Path.Combine(dependency.Directory, "package.json"), FrontendDependencies.FailureCode,
            $"[{dependency.Label}] {message}"));
    }

    private static string Code(int exitCode) => exitCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The package's files (path, size, write time) outside <c>node_modules</c>, <c>bin</c>, <c>obj</c>, hidden folders, its
    /// output folders and <c>*.tsbuildinfo</c>. Times and sizes, not contents: a poll every 300 ms reads no file.
    /// </summary>
    internal static string Fingerprint(FrontendDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        var outputs = new HashSet<string>(dependency.Outputs
            .Select(output => Path.TrimEndingDirectorySeparator(Path.GetFullPath(output, dependency.Directory))), FrontendDependencies.PathComparer);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(dependency.Directory);
        var files = new List<(string Path, long Length, long Time)>();
        while (pending.TryPop(out string? folder))
        {
            try
            {
                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    string name = Path.GetFileName(child);
                    if (name is "node_modules" or "bin" or "obj" || name.StartsWith('.') || outputs.Contains(child)) continue;
                    pending.Push(child);
                }
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (file.EndsWith(".tsbuildinfo", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    files.Add((Path.GetRelativePath(dependency.Directory, file), info.Length, info.LastWriteTimeUtc.Ticks));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A folder that went away while it was read: the next poll sees the change.
            }
        }
        foreach ((string path, long length, long time) in files.OrderBy(static file => file.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{length.ToString(CultureInfo.InvariantCulture)}\0{time.ToString(CultureInfo.InvariantCulture)}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ShortHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>Copies the package's existing output folders to <paramref name="backup"/>; false when there are none.</summary>
    internal static bool BackUp(FrontendDependency dependency, string backup)
    {
        TryDelete(backup);
        bool any = false;
        foreach (string output in dependency.Outputs)
        {
            string source = Path.GetFullPath(output, dependency.Directory);
            if (!Directory.Exists(source)) continue;
            try
            {
                CopyDirectory(source, Path.Combine(backup, Path.GetRelativePath(dependency.Directory, source)));
                any = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                TryDelete(backup);
                return false;
            }
        }
        return any;
    }

    /// <summary>Replaces the package's output folders with the copies in <paramref name="backup"/>.</summary>
    internal static bool Restore(FrontendDependency dependency, string backup)
    {
        try
        {
            foreach (string output in dependency.Outputs)
            {
                string target = Path.GetFullPath(output, dependency.Directory);
                string copy = Path.Combine(backup, Path.GetRelativePath(dependency.Directory, target));
                if (!Directory.Exists(copy)) continue;
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                CopyDirectory(copy, target);
            }
            TryDelete(backup);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await Task.WhenAll(_watches).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        _stop.Dispose();
        // A backup lives only while its rebuild runs; one that a failed restore left stays for the next start to replace.
        try
        {
            if (Directory.Exists(_stateDirectory) && !Directory.EnumerateFileSystemEntries(_stateDirectory).Any()) Directory.Delete(_stateDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A package command from the build, such as <c>bun run --bun build</c>: started without a shell when it is a plain list
/// of words, as every other command of dev, else through <c>/bin/sh -c</c> (<c>cmd.exe /d /c</c> on Windows), as MSBuild's
/// Exec runs it in the build.
/// </summary>
internal static class ShellCommand
{
    private static readonly System.Buffers.SearchValues<char> ShellCharacters = System.Buffers.SearchValues.Create("\"'`$&|;<>()*?[]{}~!#%^\\\n\r");

    internal static (string Executable, IReadOnlyList<string> Arguments) Parse(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string trimmed = command.Trim();
        if (trimmed.AsSpan().IndexOfAny(ShellCharacters) < 0)
        {
            string[] words = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return (words[0], words[1..]);
        }
        return OperatingSystem.IsWindows() ? ("cmd.exe", ["/d", "/c", trimmed]) : ("/bin/sh", ["-c", trimmed]);
    }
}

/// <summary>
/// The located errors (<c>file(line,col): error CODE: message</c>, as tsc and MSBuild print them) in a watch script's
/// output: the first one since the last <see cref="TakeNew"/>, and how long ago the last one came.
/// </summary>
internal sealed partial class WatchErrors(TimeProvider time)
{
    private readonly object _gate = new();
    private string? _first;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    internal void Observe(string line)
    {
        string key = RepeatedLineFilter.Key(line);
        if (!LocatedError().IsMatch(key)) return;
        lock (_gate)
        {
            _first ??= key;
            _last = time.GetUtcNow();
        }
    }

    /// <summary>The first error since the last call, or null.</summary>
    internal string? TakeNew()
    {
        lock (_gate)
        {
            string? first = _first;
            _first = null;
            return first;
        }
    }

    /// <summary>Whether no error came for <paramref name="period"/>.</summary>
    internal bool QuietFor(TimeSpan period)
    {
        lock (_gate) return time.GetUtcNow() - _last >= period;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\(\d+,\d+\)\s*:|:\d+:\d+\s+-)\s+error\s+[A-Za-z]+\d+\s*:")]
    private static partial System.Text.RegularExpressions.Regex LocatedError();
}

/// <summary>
/// The Views targets' cross-process lock of a frontend install or package build directory
/// (<c>RunicViewsInstallFrontendOnce</c>): a file in the temporary folder, named by a hash of the directory, opened
/// without sharing. dev holds it while it rebuilds a package, so a build of the same package waits, and the other way round.
/// </summary>
internal static class PackageBuildLock
{
    internal static string PathFor(string directory)
    {
        string full = Path.GetFullPath(directory);
        string key = OperatingSystem.IsLinux() ? full : full.ToLowerInvariant();
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(Path.GetTempPath(), "runic-install-" + hash + ".lock");
    }

    /// <summary>The open lock, or null when it could not be had within five minutes (dev then builds anyway).</summary>
    internal static async Task<FileStream?> AcquireAsync(string directory, CancellationToken cancellationToken)
    {
        string path = PathFor(directory);
        bool reported = false;
        for (var timer = Stopwatch.StartNew(); timer.Elapsed < TimeSpan.FromMinutes(5);)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (!reported) Console.WriteLine($"[dev] Waiting for another build of the frontend package in {directory}.");
                reported = true;
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }
}
