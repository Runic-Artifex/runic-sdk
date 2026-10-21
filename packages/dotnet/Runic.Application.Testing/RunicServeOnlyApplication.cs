using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace Runic.Application.Testing;

/// <summary>
/// A built Runic application running in serve-only mode for a browser test: started with
/// <c>RUNIC_APPLICATION_SERVE_ONLY=1</c> and without <c>DISPLAY</c> or <c>WAYLAND_DISPLAY</c>, it serves its
/// Window's page at <see cref="Url"/> instead of opening a window, on Runic Desktop and CS-WebUI alike.
/// </summary>
/// <remarks>
/// <code>
/// await using var app = await RunicServeOnlyApplication.StartAsync(
///     RunicServeOnlyApplication.CreateStartInfo("path/to/MyApp.dll"));
/// await page.GotoAsync(app.Url.ToString());
/// // ...
/// Assert.Equal(0, await app.StopAsync());
/// </code>
/// The application writes <c>RUNIC_APPLICATION_URL=&lt;url&gt;</c> to standard output for each Window it opens;
/// <see cref="Url"/> is the first. <see cref="StopAsync"/> writes a line to standard input, which ends serving on
/// every platform, Windows included. When the application has not exited within the timeout, its process tree is
/// killed. Standard output and standard error are captured for failure messages.
/// When the test process exits without disposing it, the application is killed on a best-effort basis: on Windows
/// through a job object, which also covers a test process that is killed itself, and elsewhere when
/// <see cref="AppDomain.ProcessExit"/> runs. A test process killed on Linux or macOS, for example with SIGKILL, can
/// leave the application running.
/// </remarks>
public sealed class RunicServeOnlyApplication : IAsyncDisposable
{
    private const string UrlPrefix = "RUNIC_APPLICATION_URL=";
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);
    // Output readers end when every process holding the pipes has exited; a descendant left running holds them.
    private static readonly TimeSpan s_outputDrainTimeout = TimeSpan.FromSeconds(2);
    // A killed process that does not exit, for example one in uninterruptible sleep, must not hang the test.
    private static readonly TimeSpan s_killTimeout = TimeSpan.FromSeconds(10);

    // The applications still running, killed when the test process exits without disposing them.
    private static readonly HashSet<RunicServeOnlyApplication> s_running = [];

    private readonly Process _process;
    private readonly WindowsJob? _job;
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _errors = new();
    private readonly TaskCompletionSource<string?> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reading;
    private readonly object _gate = new();
    private Task? _stopLine;
    private volatile bool _killed;

    static RunicServeOnlyApplication()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => KillRunning();
        if (AssemblyLoadContext.GetLoadContext(typeof(RunicServeOnlyApplication).Assembly) is { } context)
            context.Unloading += static _ => KillRunning();
    }

    private RunicServeOnlyApplication(Process process, WindowsJob? job)
    {
        _process = process;
        _job = job;
        _reading = Task.WhenAll(ReadOutputAsync(), ReadErrorsAsync());
        // A reader may still fail after the process is disposed; nothing awaits it then.
        _reading.ContinueWith(static reading => _ = reading.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The URL of the first Window's page, from <c>RUNIC_APPLICATION_URL=</c>.</summary>
    public Uri Url { get; private set; } = null!;

    /// <summary>The application's process id.</summary>
    public int ProcessId => _process.Id;

    /// <summary>The standard output written so far.</summary>
    public string StandardOutput
    {
        get { lock (_output) return _output.ToString(); }
    }

    /// <summary>The standard error written so far.</summary>
    public string StandardError
    {
        get { lock (_errors) return _errors.ToString(); }
    }

    /// <summary>Describes how to start the built application at <paramref name="applicationPath"/>.</summary>
    /// <param name="applicationPath">
    /// The application's executable, or its <c>.dll</c>, which runs with <c>dotnet</c>. Its directory becomes the
    /// working directory.
    /// </param>
    /// <returns>Start information to pass to <see cref="StartAsync"/>, after adding arguments or environment variables.</returns>
    public static ProcessStartInfo CreateStartInfo(string applicationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        var path = Path.GetFullPath(applicationPath);
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) };
        // DOTNET_HOST_PATH is the dotnet that runs the test, when the .NET SDK started it.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        return new ProcessStartInfo(dotnet, [path]) { WorkingDirectory = Path.GetDirectoryName(path) };
    }

    /// <summary>Starts an application in serve-only mode and waits for its URL.</summary>
    /// <param name="startInfo">
    /// How to start the application, from <see cref="CreateStartInfo"/> or your own. Its file name, arguments, working
    /// directory and environment are used, and it is not changed: the application runs with
    /// <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, without <c>DISPLAY</c> and <c>WAYLAND_DISPLAY</c>, and with redirected
    /// standard streams.
    /// </param>
    /// <param name="timeout">How long to wait for the URL; the default is 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait; the application is then killed.</param>
    /// <returns>The serving application. Dispose it to stop it.</returns>
    /// <exception cref="InvalidOperationException">The application exited before it wrote its URL.</exception>
    /// <exception cref="TimeoutException">The application did not write its URL within <paramref name="timeout"/>; it was killed.</exception>
    public static async Task<RunicServeOnlyApplication> StartAsync(ProcessStartInfo startInfo, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var wait = timeout ?? s_defaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero, nameof(timeout));
        var start = ServeOnlyStartInfo(startInfo);
        var job = OperatingSystem.IsWindows() ? WindowsJob.TryCreate() : null;
        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException($"The application '{start.FileName}' did not start.");
        }
        catch
        {
            job?.Dispose();
            throw;
        }
        job?.TryAssign(process);
        var application = new RunicServeOnlyApplication(process, job);
        lock (s_running) s_running.Add(application);
        try
        {
            string? url;
            try { url = await application._url.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                await application.KillAsync().ConfigureAwait(false);
                throw new TimeoutException(application.Describe($"The application did not write {UrlPrefix} within {wait}; it was killed."));
            }
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                await application.KillAsync().ConfigureAwait(false);
                throw new InvalidOperationException(application.Describe(url is null
                    ? $"The application exited with {application.ExitCodeText()} before it wrote {UrlPrefix}."
                    : $"The application wrote an invalid URL: '{url}'."));
            }
            application.Url = uri;
            return application;
        }
        catch
        {
            // Cancelled, timed out or failed: nothing is left running.
            await application.KillAsync().ConfigureAwait(false);
            application.Release();
            throw;
        }
    }

    // A copy of the caller's start information with the serve-only contract applied.
    private static ProcessStartInfo ServeOnlyStartInfo(ProcessStartInfo startInfo)
    {
        var start = new ProcessStartInfo(startInfo.FileName)
        {
            WorkingDirectory = startInfo.WorkingDirectory,
            CreateNoWindow = startInfo.CreateNoWindow,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (var argument in startInfo.ArgumentList) start.ArgumentList.Add(argument);
        }
        else start.Arguments = startInfo.Arguments;
        start.Environment.Clear();
        foreach (var (name, value) in startInfo.Environment) start.Environment[name] = value;
        start.Environment["RUNIC_APPLICATION_SERVE_ONLY"] = "1";
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        return start;
    }

    /// <summary>
    /// Lists the application's running descendant processes: its children, their children, and so on.
    /// </summary>
    /// <remarks>
    /// List them while the application runs: a process whose parent has exited is no longer its descendant. After
    /// <see cref="StopAsync"/>, <see cref="RunicDescendantProcess.HasExited"/> tells whether the application ended
    /// each one. The snapshots hold no process handle.
    /// </remarks>
    public IReadOnlyList<RunicDescendantProcess> GetDescendantProcesses()
    {
        var parents = ProcessTree.ReadParents();
        var descendants = new List<RunicDescendantProcess>();
        var pending = new Queue<(int Id, DateTime? Started)>([(_process.Id, StartTimeOf(_process))]);
        var seen = new HashSet<int> { _process.Id };
        while (pending.TryDequeue(out var parent))
        {
            foreach (var (child, childParent) in parents)
            {
                if (childParent != parent.Id || !seen.Add(child)) continue;
                DateTime? started;
                try
                {
                    using var process = Process.GetProcessById(child);
                    started = StartTimeOf(process);
                }
                catch (ArgumentException) { continue; } // It has exited meanwhile.
                // Windows keeps a process's parent id after the parent exits and reuses ids, so the recorded
                // parent may be an unrelated, newer process with the old id. A child is only accepted when it
                // started after its parent, as Process.Kill(entireProcessTree: true) checks.
                if (OperatingSystem.IsWindows() &&
                    (started is not { } childStarted || parent.Started is not { } parentStarted || childStarted <= parentStarted))
                {
                    continue;
                }
                descendants.Add(new RunicDescendantProcess(child, started));
                pending.Enqueue((child, started));
            }
        }
        return descendants;
    }

    private static DateTime? StartTimeOf(Process process)
    {
        try { return process.StartTime; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // Exited, or not accessible.
        }
    }

    /// <summary>
    /// Stops serving by writing a line to standard input and waits for the application to exit. When it has not
    /// exited within <paramref name="timeout"/>, its process tree is killed and <see cref="TimeoutException"/> is thrown.
    /// </summary>
    /// <remarks>
    /// The line is written once. A further call, for example with a longer timeout after a cancelled one, waits for
    /// the same exit with its own timeout, and returns the exit code at once when the application has exited. After a
    /// timeout killed the application, every call throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <param name="timeout">How long to wait for the exit; the default is 30 seconds.</param>
    /// <param name="cancellationToken">Cancels this wait; the application keeps stopping, and disposing it ends it.</param>
    /// <returns>The application's exit code.</returns>
    /// <exception cref="TimeoutException">The application did not exit in time and was killed.</exception>
    /// <exception cref="InvalidOperationException">The application was killed earlier, after it did not exit in time.</exception>
    public async Task<int> StopAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var wait = timeout ?? s_defaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero, nameof(timeout));
        Task stopLine;
        lock (_gate) stopLine = _stopLine ??= WriteStopLineAsync();
        await stopLine.ConfigureAwait(false);
        try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await KillAsync().ConfigureAwait(false);
            throw new TimeoutException(Describe($"The application did not exit within {wait} after the stop line; its process tree was killed."));
        }
        if (_killed) throw new InvalidOperationException(Describe("The application was killed after it did not exit in time."));
        await DrainOutputAsync().ConfigureAwait(false);
        lock (s_running) s_running.Remove(this);
        return _process.ExitCode;
    }

    private async Task WriteStopLineAsync()
    {
        try
        {
            await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException) { } // The application has exited and closed its input.
    }

    /// <summary>
    /// Stops the application like <see cref="StopAsync"/> and releases it. An application that does not exit in time
    /// is killed with its process tree without an exception; call <see cref="StopAsync"/> to see a shutdown that hangs.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch (TimeoutException) { } // Killed.
        catch (InvalidOperationException) { } // Killed earlier.
        finally { Release(); }
    }

    private void Release()
    {
        lock (s_running) s_running.Remove(this);
        // Closing the job kills what is left in it, such as a descendant the application leaves running.
        _job?.Dispose();
        _process.Dispose();
    }

    private async Task KillAsync()
    {
        _killed = true;
        TryKillTree(_process);
        try { await _process.WaitForExitAsync().WaitAsync(s_killTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await DrainOutputAsync().ConfigureAwait(false);
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException)
        {
            // It has exited, or part of its tree could not be killed; the caller reports the application's output.
        }
    }

    private static void KillRunning()
    {
        RunicServeOnlyApplication[] running;
        lock (s_running) running = [.. s_running];
        foreach (var application in running)
        {
            try { if (!application._process.HasExited) TryKillTree(application._process); }
            catch (InvalidOperationException) { } // Disposed meanwhile.
        }
    }

    private async Task DrainOutputAsync()
    {
        try { await _reading.WaitAsync(s_outputDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_output) _output.AppendLine(line);
                if (line.StartsWith(UrlPrefix, StringComparison.Ordinal)) _url.TrySetResult(line[UrlPrefix.Length..].Trim());
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { _url.TrySetResult(null); }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (_errors) _errors.AppendLine(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string ExitCodeText()
    {
        try { return _process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "an unknown code"; }
    }

    private string Describe(string message) =>
        $"{message}\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}";

    // A Windows job object that kills its processes when its last handle closes, including when the test process
    // ends without disposing the application. Best effort: without it, the application only lacks that guarantee.
    private sealed class WindowsJob : IDisposable
    {
        private nint _handle;

        private WindowsJob(nint handle) => _handle = handle;

        public static WindowsJob? TryCreate()
        {
            var handle = CreateJobObjectW(0, null);
            if (handle == 0) return null;
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                return new WindowsJob(handle);
            CloseHandle(handle);
            return null;
        }

        // The application's own children inherit the job; a process started before this call escapes it.
        public void TryAssign(Process process)
        {
            try { _ = AssignProcessToJobObject(_handle, process.Handle); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0) CloseHandle(handle);
        }

        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformationClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }

    // Parent process ids of every running process.
    private static class ProcessTree
    {
        public static List<(int Process, int Parent)> ReadParents()
        {
            if (OperatingSystem.IsLinux()) return ReadProc();
            if (OperatingSystem.IsWindows()) return ReadToolhelp();
            return ReadPs();
        }

        // /proc/<pid>/stat: "pid (comm) state ppid ..."; comm may contain spaces and parentheses.
        private static List<(int, int)> ReadProc()
        {
            var parents = new List<(int, int)>();
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                string stat;
                try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                    parents.Add((pid, parent));
            }
            return parents;
        }

        private static List<(int, int)> ReadPs()
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", ["-A", "-o", "pid=", "-o", "ppid="])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("ps did not start.");
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            var parents = new List<(int, int)>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 &&
                    int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                    int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                    parents.Add((pid, parent));
            }
            return parents;
        }

        private static List<(int, int)> ReadToolhelp()
        {
            var parents = new List<(int, int)>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == InvalidHandle) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    parents.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            }
            finally { CloseHandle(snapshot); }
            return parents;
        }

        private const uint SnapProcess = 0x2;
        private static readonly nint InvalidHandle = -1;

        // PROCESSENTRY32W; the executable name is not read.
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            public ExecutableName ExeFile;
        }

        [InlineArray(260)]
        private struct ExecutableName
        {
            private ushort _first;
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

        [DllImport("kernel32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
