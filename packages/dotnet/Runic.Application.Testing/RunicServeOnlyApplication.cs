using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// </remarks>
public sealed class RunicServeOnlyApplication : IAsyncDisposable
{
    private const string UrlPrefix = "RUNIC_APPLICATION_URL=";
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);
    // Output readers end when every process holding the pipes has exited; a descendant left running holds them.
    private static readonly TimeSpan s_outputDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _errors = new();
    private readonly TaskCompletionSource<string?> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reading;
    private readonly object _gate = new();
    private Task<int>? _stopping;

    private RunicServeOnlyApplication(Process process)
    {
        _process = process;
        _reading = Task.WhenAll(ReadOutputAsync(), ReadErrorsAsync());
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
    /// How to start the application, from <see cref="CreateStartInfo"/> or your own. This method sets
    /// <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, removes <c>DISPLAY</c> and <c>WAYLAND_DISPLAY</c>, and redirects the
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
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.Environment["RUNIC_APPLICATION_SERVE_ONLY"] = "1";
        startInfo.Environment.Remove("DISPLAY");
        startInfo.Environment.Remove("WAYLAND_DISPLAY");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The application '{startInfo.FileName}' did not start.");
        var application = new RunicServeOnlyApplication(process);
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
        catch (Exception error) when (error is not TimeoutException and not InvalidOperationException)
        {
            // Cancelled, for example: nothing is left running.
            await application.KillAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists the application's running descendant processes: its children, their children, and so on.
    /// </summary>
    /// <remarks>
    /// List them while the application runs: a process whose parent has exited is no longer its descendant. After
    /// <see cref="StopAsync"/>, <see cref="Process.HasExited"/> tells whether the application ended each one.
    /// Dispose the returned processes.
    /// </remarks>
    public IReadOnlyList<Process> GetDescendantProcesses()
    {
        var parents = ProcessTree.ReadParents();
        var descendants = new List<Process>();
        var pending = new Queue<int>([_process.Id]);
        var seen = new HashSet<int> { _process.Id };
        while (pending.TryDequeue(out var parent))
        {
            foreach (var (child, childParent) in parents)
            {
                if (childParent != parent || !seen.Add(child)) continue;
                pending.Enqueue(child);
                try { descendants.Add(Process.GetProcessById(child)); }
                catch (ArgumentException) { } // It has exited meanwhile.
            }
        }
        return descendants;
    }

    /// <summary>
    /// Stops serving by writing a line to standard input and waits for the application to exit. When it has not
    /// exited within <paramref name="timeout"/>, its process tree is killed and <see cref="TimeoutException"/> is thrown.
    /// </summary>
    /// <param name="timeout">How long to wait for the exit; the default is 30 seconds.</param>
    /// <returns>The application's exit code.</returns>
    /// <exception cref="TimeoutException">The application did not exit in time and was killed.</exception>
    public Task<int> StopAsync(TimeSpan? timeout = null)
    {
        var wait = timeout ?? s_defaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero, nameof(timeout));
        lock (_gate) return _stopping ??= StopCoreAsync(wait);
    }

    private async Task<int> StopCoreAsync(TimeSpan timeout)
    {
        try
        {
            await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException) { } // The application has exited and closed its input.
        try { await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await KillAsync().ConfigureAwait(false);
            throw new TimeoutException(Describe($"The application did not exit within {timeout} after the stop line; its process tree was killed."));
        }
        await DrainOutputAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>Stops the application, killing its process tree when it does not exit in time, and releases it.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch (TimeoutException) { } // Killed; the test reports its own failure.
        finally { _process.Dispose(); }
    }

    private async Task KillAsync()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // It has exited.
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await DrainOutputAsync().ConfigureAwait(false);
    }

    private async Task DrainOutputAsync()
    {
        try { await _reading.WaitAsync(s_outputDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
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
        finally { _url.TrySetResult(null); }
    }

    private async Task ReadErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            lock (_errors) _errors.AppendLine(line);
    }

    private string ExitCodeText()
    {
        try { return _process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "an unknown code"; }
    }

    private string Describe(string message) =>
        $"{message}\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}";

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
            if (snapshot == InvalidHandle) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
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
