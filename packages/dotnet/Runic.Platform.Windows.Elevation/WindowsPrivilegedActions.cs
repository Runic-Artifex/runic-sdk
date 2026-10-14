using System.Collections.Frozen;
using System.Runtime.ExceptionServices;

namespace Runic.Platform.Windows.Elevation;

/// <summary>Where the helpers are and how the elevation prompt is shown.</summary>
/// <remarks>
/// The generated <c>PrivilegedActions.WindowsElevation</c> holds the <c>WindowsProgram</c> metadata of the
/// <c>RunicPrivilegedAction</c> items; add the other settings with <c>with</c>.
/// </remarks>
public sealed record WindowsElevationOptions
{
    /// <summary>
    /// The directory relative helper paths start from, such as <c>C:\Program Files\Example</c>. Null is the
    /// application's own directory (<see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public string? ApplicationDirectory { get; init; }

    /// <summary>
    /// The helper of each action by action ID, from its <c>WindowsProgram</c> metadata: a path relative to
    /// <see cref="ApplicationDirectory"/> or an absolute path. An action without an entry uses the file name of its
    /// declared program with <c>.exe</c>, in <see cref="ApplicationDirectory"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Programs { get; init; }

    /// <summary>The window that owns the UAC prompt (an <c>HWND</c>), so the prompt comes to the front; zero for none.</summary>
    public nint OwnerWindow { get; init; }

    /// <summary>
    /// How long a running action may take to stop after cancellation before its process is terminated. Null, the
    /// default, never terminates it: as on Linux, the result reports how the action actually ended. When the process
    /// cannot be terminated (the handle UAC gives a non-elevated application may not allow it), the result is
    /// <see cref="PlatformFailureCode.PermissionDenied"/> and the helper may still run.
    /// </summary>
    public TimeSpan? TerminateAfterCancellation { get; init; }
}

/// <summary>Runs declared privileged actions on Windows through a helper elevated with UAC.</summary>
/// <remarks>
/// <para>
/// Each run starts the action's helper with <c>ShellExecuteExW</c> and the <c>runas</c> verb, which shows the UAC
/// prompt. The helper connects to a named pipe the application created for this run, reports whether it runs
/// elevated and accepts the action, and streams its output back; the application cancels over the same pipe. The
/// helper must be a NativeAOT build that calls <see cref="ElevatedHelper.Match"/>, installed in a directory only
/// administrators can change, such as Program Files.
/// </para>
/// <para>
/// UAC reports a declined prompt and a standard user's closed credential prompt the same way, so both are
/// <see cref="PlatformFailureCode.UserDismissed"/>; <see cref="PlatformFailureCode.PermissionDenied"/> means a policy
/// refused the elevation. With UAC off, <c>runas</c> starts the helper without elevation; it reports that, and the
/// result is <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
/// </para>
/// </remarks>
public sealed class WindowsPrivilegedActions : IDesktopPrivilegedActions
{
    /// <summary>The exit code of a helper terminated after <see cref="WindowsElevationOptions.TerminateAfterCancellation"/>: <c>ERROR_CANCELLED</c>.</summary>
    public const int TerminatedExitCode = ElevationOutcome.ErrorCancelled;

    private readonly FrozenDictionary<string, (PrivilegedActionDeclaration Action, string Helper)> _actions;
    private readonly WindowsElevationOptions _options;
    private readonly IElevationHost _host;

    internal WindowsPrivilegedActions(IEnumerable<PrivilegedActionDeclaration> actions, WindowsElevationOptions options, string applicationDirectory, IElevationHost host)
    {
        _options = options;
        _host = host;
        var helpers = new Dictionary<string, (PrivilegedActionDeclaration, string)>(StringComparer.Ordinal);
        foreach (var action in Index(actions))
        {
            string? program = null;
            options.Programs?.TryGetValue(action.Id, out program);
            var helper = HelperLocation.Resolve(action, program, applicationDirectory)
                ?? throw new ArgumentException($"The Windows helper of '{action.Id}' is not a local path without '.' or '..'.", nameof(options));
            helpers.Add(action.Id, (action, helper));
        }
        foreach (var id in options.Programs?.Keys ?? [])
            if (!helpers.ContainsKey(id)) throw new ArgumentException($"'{id}' in Programs is not a declared action.", nameof(options));
        _actions = helpers.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Creates the provider for <paramref name="actions"/>, usually the generated <c>PrivilegedActions.All</c>.</summary>
    /// <param name="actions">The declared actions.</param>
    /// <param name="options">Usually the generated <c>PrivilegedActions.WindowsElevation</c>.</param>
    /// <remarks>Off Windows, the result reports every request unavailable with <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    /// <exception cref="ArgumentException">
    /// Two actions share an ID or a verb, an action without a verb is not the only one, or a helper path or
    /// <see cref="WindowsElevationOptions.ApplicationDirectory"/> is not a local, normalized path.
    /// </exception>
    public static IDesktopPrivilegedActions Create(IEnumerable<PrivilegedActionDeclaration> actions, WindowsElevationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        options ??= new WindowsElevationOptions();
        if (options.TerminateAfterCancellation is { } wait && (wait < TimeSpan.Zero || wait.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(options), "TerminateAfterCancellation must be a non-negative time span.");
        var list = actions.ToList();
        if (!OperatingSystem.IsWindows())
        {
            _ = Index(list);
            return DesktopPrivilegedActions.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        var directory = options.ApplicationDirectory ?? AppContext.BaseDirectory;
        if (!HelperLocation.IsValidDirectory(directory))
            throw new ArgumentException("ApplicationDirectory must be a local absolute path such as C:\\Program Files\\Example.", nameof(options));
        return new WindowsPrivilegedActions(list, options, directory, WindowsElevationHost.Instance);
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Action);
        if (request.Arguments.IsDefault) throw new ArgumentException("Arguments must be initialized.", nameof(request));
        if (!_actions.TryGetValue(request.Action.Id, out var entry) || !SameDeclaration(entry.Action, request.Action))
            throw new ArgumentException($"'{request.Action.Id}' is not an action this provider declares.", nameof(request));
        var argv = entry.Action.CreateArgumentVector(request.Arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (_host.CheckProgram(entry.Helper) is var check && check != ProgramCheck.Trusted)
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, ElevationOutcome.ProgramDiagnostic(check));

        var pipeName = ElevationProtocol.NewPipeName();
        var commandLine = WindowsCommandLine.Build(ElevationProtocol.HelperArguments(pipeName, argv));
        // ShellExecuteExW adds the quoted program and a space.
        if (commandLine.Length + entry.Helper.Length + 3 > WindowsCommandLine.MaximumLength)
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
                new(ElevationOutcome.Domain, 6, "The arguments are too long for a Windows command line."));

        IElevationPipe pipe;
        try { pipe = _host.CreatePipe(pipeName); }
        catch (IOException e)
        {
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new("Win32", e.HResult & 0xFFFF, e.Message));
        }
        return await new ElevationRun(_host, pipe, _options, request.Output).RunAsync(entry.Helper, commandLine, cancellationToken).ConfigureAwait(false);
    }

    private static bool SameDeclaration(PrivilegedActionDeclaration a, PrivilegedActionDeclaration b) =>
        ReferenceEquals(a, b) || (a.Program == b.Program && a.Verb == b.Verb && a.MaximumArguments == b.MaximumArguments && a.ArgumentPattern == b.ArgumentPattern);

    private static List<PrivilegedActionDeclaration> Index(IEnumerable<PrivilegedActionDeclaration> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var byId = new Dictionary<string, PrivilegedActionDeclaration>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            // The helper sees only its arguments, so the verb names exactly one action (RUNICPA003 at build time).
            if (byId.Values.FirstOrDefault(a => a.Verb == action.Verb || a.Verb is null || action.Verb is null) is { } clash)
                throw new ArgumentException($"Actions '{clash.Id}' and '{action.Id}' cannot be told apart by their first argument; give each its own verb.", nameof(actions));
            if (!byId.TryAdd(action.Id, action)) throw new ArgumentException($"Action '{action.Id}' is declared twice.", nameof(actions));
        }
        return [.. byId.Values];
    }
}

/// <summary>One run: the prompt, the helper's connection and handshake, its output and its end.</summary>
internal sealed class ElevationRun(IElevationHost host, IElevationPipe pipe, WindowsElevationOptions options, Action<PrivilegedActionOutput>? callback)
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _writeLock = new();

    public async Task<PlatformResult<PrivilegedActionCompletion>> RunAsync(string helper, string commandLine, CancellationToken cancellationToken)
    {
        // ShellExecuteExW blocks until the user answers the prompt, and nothing can withdraw a UAC prompt. On
        // cancellation the run stops waiting and closes the pipe: a helper started by a later approval finds no pipe
        // and exits without acting, and is terminated as soon as its handle arrives.
        var launch = host.LaunchAsync(helper, commandLine, options.OwnerWindow);
        try { await launch.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            _ = launch.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Process is { } late)
                {
                    late.TryTerminate(WindowsPrivilegedActions.TerminatedExitCode);
                    late.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
        var launched = await launch.ConfigureAwait(false);
        if (launched.Process is not { } process)
        {
            pipe.Dispose();
            return ElevationOutcome.FromLaunchError(launched.Error);
        }
        using (process)
        {
            Stream? stream;
            try { stream = await pipe.AcceptAsync(process.Id, process.Exited, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopBeforeStart(process);
                throw;
            }
            if (stream is null)
            {
                pipe.Dispose();
                var code = await process.Exited.ConfigureAwait(false);
                return ElevationOutcome.ProtocolFailure($"The helper exited with code {code} before it connected.");
            }
            try
            {
                return await RunConnectedAsync(process, stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                pipe.Dispose();
            }
        }
    }

    private void StopBeforeStart(IHelperProcess process)
    {
        // The helper has not been told to go: closing the pipe makes it exit without acting.
        pipe.Dispose();
        process.TryTerminate(WindowsPrivilegedActions.TerminatedExitCode);
    }

    private async Task<PlatformResult<PrivilegedActionCompletion>> RunConnectedAsync(IHelperProcess process, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ElevationProtocol.MaximumPayload];
        HelperStatus? status;
        try
        {
            var frame = await ElevationProtocol.ReadFrameAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            status = frame is (ElevationProtocol.Hello, var length) ? ElevationProtocol.ParseHello(buffer.AsSpan(0, length)) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopBeforeStart(process);
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            status = null;
        }
        if (status is null) return ElevationOutcome.ProtocolFailure("The helper closed the connection or sent no valid greeting.");
        if (status != HelperStatus.Accepted) return ElevationOutcome.FromStatus(status.Value);

        if (cancellationToken.IsCancellationRequested)
        {
            StopBeforeStart(process);
            cancellationToken.ThrowIfCancellationRequested();
        }
        try { Send(stream, ElevationProtocol.Go); }
        catch (IOException) { return ElevationOutcome.ProtocolFailure("The helper closed the connection before the action started."); }

        // The action runs. Cancellation asks it to stop and, if configured, terminates it after a while; the result
        // reports how it ended either way.
        var output = new OutputSink(callback);
        var reader = Task.Run(() => ReadOutputAsync(stream, output, buffer), CancellationToken.None);
        using var terminate = new CancellationTokenSource();
        // Completes when TerminateAfterCancellation elapsed and the helper could not be terminated: the handle UAC
        // returns to a non-elevated caller may lack PROCESS_TERMINATE.
        var notTerminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() =>
        {
            try { Send(stream, ElevationProtocol.Cancel); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            if (options.TerminateAfterCancellation is { } wait)
                _ = Task.Delay(wait, terminate.Token).ContinueWith(async t =>
                {
                    if (!t.IsCompletedSuccessfully || process.TryTerminate(WindowsPrivilegedActions.TerminatedExitCode)) return;
                    // It may have exited just now; otherwise it runs on.
                    try { await process.Exited.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (TimeoutException) { notTerminated.TrySetResult(); }
                }, TaskScheduler.Default);
        }))
        {
            if (await Task.WhenAny(process.Exited, notTerminated.Task).ConfigureAwait(false) != process.Exited)
            {
                output.Close();
                return ElevationOutcome.NotTerminated();
            }
            var exitCode = await process.Exited.ConfigureAwait(false);
            await terminate.CancelAsync().ConfigureAwait(false);
            try { await reader.WaitAsync(OutputDrainTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { }
            output.Close();
            output.ThrowIfFailed();
            return new PlatformResult<PrivilegedActionCompletion>.Success(new PrivilegedActionCompletion(exitCode));
        }
    }

    private void Send(Stream stream, byte type)
    {
        lock (_writeLock) ElevationProtocol.WriteFrame(stream, type, []);
    }

    private static async Task ReadOutputAsync(Stream stream, OutputSink output, byte[] buffer)
    {
        var stdout = new LineSplitter(line => output.Deliver(new(PrivilegedOutputChannel.StandardOutput, line)));
        var stderr = new LineSplitter(line => output.Deliver(new(PrivilegedOutputChannel.StandardError, line)));
        try
        {
            // The callback runs before the next frame is read, so a slow callback blocks the helper's writes.
            while (await ElevationProtocol.ReadFrameAsync(stream, buffer, CancellationToken.None).ConfigureAwait(false) is var (type, length))
            {
                if (type == ElevationProtocol.StandardOutput) stdout.Push(buffer.AsSpan(0, length));
                else if (type == ElevationProtocol.StandardError) stderr.Push(buffer.AsSpan(0, length));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException) { }
        stdout.Flush();
        stderr.Flush();
    }

    /// <summary>Delivers output lines one at a time and keeps the first exception a callback threw.</summary>
    private sealed class OutputSink(Action<PrivilegedActionOutput>? callback)
    {
        private readonly Lock _lock = new();
        private Exception? _failure;
        private bool _closed;

        public void Close()
        {
            lock (_lock) _closed = true;
        }

        public void Deliver(PrivilegedActionOutput line)
        {
            if (callback is null) return;
            lock (_lock)
            {
                if (_failure is not null || _closed) return;
                try { callback(line); }
                catch (Exception e) { _failure = e; }
            }
        }

        public void ThrowIfFailed()
        {
            if (_failure is { } failure) ExceptionDispatchInfo.Throw(failure);
        }
    }
}

/// <summary>The helper process the elevation started.</summary>
internal interface IHelperProcess : IDisposable
{
    int Id { get; }
    /// <summary>Completes with the exit code when the process exits.</summary>
    Task<int> Exited { get; }
    /// <summary>Terminates the process; false when the handle does not allow it or it already exited.</summary>
    bool TryTerminate(int exitCode);
}

/// <summary>A started helper, or the Win32 error of a launch that started nothing.</summary>
internal readonly record struct LaunchResult(IHelperProcess? Process, int Error);

/// <summary>The application's end of the pipe, created before the helper starts.</summary>
internal interface IElevationPipe : IDisposable
{
    /// <summary>
    /// Waits for the process <paramref name="clientProcessId"/> to connect and returns the connected stream;
    /// disconnects any other client. Null when <paramref name="processExited"/> completes and nothing connected.
    /// The pipe listens from its creation, so a helper that connects and exits at once still counts as connected.
    /// </summary>
    Task<Stream?> AcceptAsync(int clientProcessId, Task processExited, CancellationToken cancellationToken);
}

/// <summary>The operating-system side of a run, replaceable in tests.</summary>
internal interface IElevationHost
{
    /// <summary>Whether only administrators can replace the helper at <paramref name="path"/>.</summary>
    ProgramCheck CheckProgram(string path);

    /// <summary>Creates the pipe <paramref name="name"/>, failing if it exists.</summary>
    /// <exception cref="IOException">The pipe could not be created.</exception>
    IElevationPipe CreatePipe(string name);

    /// <summary>Starts the helper elevated; completes when the prompt was answered.</summary>
    Task<LaunchResult> LaunchAsync(string program, string commandLine, nint ownerWindow);
}
