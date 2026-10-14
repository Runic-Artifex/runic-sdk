using System.Collections.Frozen;
using System.Diagnostics;

namespace Runic.Platform.Linux.Polkit;

/// <summary>How privileged actions reach pkexec.</summary>
public sealed record PolkitOptions
{
    /// <summary>
    /// The absolute path of a setuid-root pkexec. Null searches <c>/run/wrappers/bin/pkexec</c> (NixOS),
    /// <c>/usr/bin/pkexec</c> and <c>/bin/pkexec</c>, never <c>PATH</c>.
    /// </summary>
    public string? PkexecPath { get; init; }

    /// <summary>
    /// Whether pkexec may ask for the password on the controlling terminal when no graphical authentication agent
    /// is running. Off by default, so an application without an agent reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> instead of prompting in a terminal it may not show.
    /// </summary>
    public bool AllowTerminalAuthentication { get; init; }
}

/// <summary>Runs declared privileged actions through pkexec, authorized by the action's Polkit policy.</summary>
/// <remarks>
/// The policy, generated from <c>RunicPrivilegedAction</c> items, must be installed in
/// <c>/usr/share/polkit-1/actions</c> or the distribution's equivalent. Without it pkexec falls back to its generic
/// <c>org.freedesktop.policykit.exec</c> action, which asks for an administrator with a generic message.
/// </remarks>
public sealed class PolkitPrivilegedActions : IDesktopPrivilegedActions
{
    // A pkexec diagnostic is a few short lines before the program starts.
    private const int DiagnosticCharacters = 4096;
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly FrozenDictionary<string, PrivilegedActionDeclaration> _actions;
    private readonly PolkitOptions _options;
    private readonly IPolkitHost _host;

    internal PolkitPrivilegedActions(IEnumerable<PrivilegedActionDeclaration> actions, PolkitOptions options, IPolkitHost host)
    {
        _actions = Index(actions);
        _options = options;
        _host = host;
    }

    /// <summary>Creates the provider for <paramref name="actions"/>, usually the generated <c>PrivilegedActions.All</c>.</summary>
    /// <remarks>Off Linux, the result reports every request unavailable with <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    /// <exception cref="ArgumentException">Two actions share an ID, or the same program and verb.</exception>
    public static IDesktopPrivilegedActions Create(IEnumerable<PrivilegedActionDeclaration> actions, PolkitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        options ??= new PolkitOptions();
        if (options.PkexecPath is { } path && !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The pkexec path must be absolute.", nameof(options));
        var indexed = Index(actions);
        if (!OperatingSystem.IsLinux()) return DesktopPrivilegedActions.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return new PolkitPrivilegedActions(indexed.Values, options, LinuxPolkitHost.Instance);
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Action);
        if (request.Arguments.IsDefault) throw new ArgumentException("Arguments must be initialized.", nameof(request));
        if (!_actions.TryGetValue(request.Action.Id, out var action) || !SameDeclaration(action, request.Action))
            throw new ArgumentException($"'{request.Action.Id}' is not an action this provider declares.", nameof(request));
        var argv = action.CreateArgumentVector(request.Arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (_host.FindPkexec(_options.PkexecPath) is not { } pkexec)
            return new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        if (_host.CheckProgram(action.Program) is var check && check != ProgramCheck.Trusted)
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, ProgramDiagnostic(check));

        var start = new ProcessStartInfo(pkexec)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!_options.AllowTerminalAuthentication) start.ArgumentList.Add("--disable-internal-agent");
        start.ArgumentList.Add(action.Program);
        foreach (var argument in argv) start.ArgumentList.Add(argument);

        var run = await PkexecRun.StartAsync(start).ConfigureAwait(false);
        if (run is null)
            return new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        using var process = run.Process;

        var output = new OutputSink(request.Output);
        var stdout = LineReader.PumpAsync(process.StandardOutput.BaseStream, line => output.Deliver(new(PrivilegedOutputChannel.StandardOutput, line)));
        var diagnostic = new DiagnosticCapture(DiagnosticCharacters);
        var stderr = LineReader.PumpAsync(process.StandardError.BaseStream, line =>
        {
            diagnostic.Add(line);
            output.Deliver(new(PrivilegedOutputChannel.StandardError, line));
        });

        var stoppedBeforeStart = false;
        using (cancellationToken.Register(() =>
        {
            if (run.Exited.IsCompleted) return;
            if (_host.TryStopBeforeStart(process))
            {
                Volatile.Write(ref stoppedBeforeStart, true);
                return;
            }
            // The program runs as root and cannot be signalled; closing its input asks it to stop.
            try { process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }))
        {
            var exitCode = await run.Exited.ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(OutputDrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A process the program started still holds its output open; stop reading when the run ends.
            }
            output.Close();
            output.ThrowIfFailed();
            if (Volatile.Read(ref stoppedBeforeStart)) throw new OperationCanceledException(cancellationToken);
            return PkexecOutcome.Classify(exitCode, diagnostic.Text);
        }
    }

    private static PlatformDiagnostic ProgramDiagnostic(ProgramCheck check) => check switch
    {
        ProgramCheck.Missing => new("Runic.PrivilegedAction", 1, "The declared program does not exist."),
        ProgramCheck.NotRealPath => new("Runic.PrivilegedAction", 2, "The declared program path contains a symbolic link, so pkexec would not match its policy."),
        _ => new("Runic.PrivilegedAction", 3, "The declared program, or a directory above it, is not owned by root or can be changed by other users."),
    };

    private static bool SameDeclaration(PrivilegedActionDeclaration a, PrivilegedActionDeclaration b) =>
        ReferenceEquals(a, b) || (a.Program == b.Program && a.Verb == b.Verb && a.MaximumArguments == b.MaximumArguments && a.ArgumentPattern == b.ArgumentPattern);

    private static FrozenDictionary<string, PrivilegedActionDeclaration> Index(IEnumerable<PrivilegedActionDeclaration> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var byId = new Dictionary<string, PrivilegedActionDeclaration>(StringComparer.Ordinal);
        var byProgram = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            if (!byId.TryAdd(action.Id, action)) throw new ArgumentException($"Action '{action.Id}' is declared twice.", nameof(actions));
            // pkexec picks the first action whose program matches and whose verb, when set, matches argv[1].
            var verbs = byProgram.TryGetValue(action.Program, out var existing) ? existing : byProgram[action.Program] = [];
            if (verbs.Contains(action.Verb) || (verbs.Count > 0 && (action.Verb is null || verbs.Contains(null))))
                throw new ArgumentException($"Action '{action.Id}' runs the same program as another action; give each a distinct verb.", nameof(actions));
            verbs.Add(action.Verb);
        }
        return byId.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>pkexec, started from a thread that lives until it exits.</summary>
    /// <remarks>
    /// pkexec sets <c>PR_SET_PDEATHSIG</c>, so the program receives SIGTERM when the thread that forked it ends. A
    /// thread-pool thread can retire while a long action runs; this dedicated thread waits for the exit instead.
    /// </remarks>
    private sealed class PkexecRun
    {
        private PkexecRun(Process process, Task<int> exited) { Process = process; Exited = exited; }
        public Process Process { get; }
        public Task<int> Exited { get; }

        public static Task<PkexecRun?> StartAsync(ProcessStartInfo start)
        {
            var started = new TaskCompletionSource<PkexecRun?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Process? process;
                try { process = Process.Start(start); }
                catch (System.ComponentModel.Win32Exception)
                {
                    started.SetResult(null);
                    return;
                }
                if (process is null)
                {
                    started.SetResult(null);
                    return;
                }
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                started.SetResult(new PkexecRun(process, exited.Task));
                process.WaitForExit();
                exited.SetResult(process.ExitCode);
            })
            {
                IsBackground = true,
                Name = "Runic pkexec",
            };
            thread.Start();
            return started.Task;
        }
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
            if (_failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    private sealed class DiagnosticCapture(int limit)
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        public void Add(string line)
        {
            lock (_text)
            {
                if (_text.Length >= limit) return;
                _text.Append(line.AsSpan(0, Math.Min(line.Length, limit - _text.Length))).Append('\n');
            }
        }
    }
}
