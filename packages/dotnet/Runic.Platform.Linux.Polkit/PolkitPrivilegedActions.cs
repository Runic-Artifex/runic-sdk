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
    /// <exception cref="ArgumentException">Two actions share an ID or a verb, or an action without a verb is not the only one.</exception>
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
        SanitizeEnvironment(start.Environment);
        if (!_options.AllowTerminalAuthentication) start.ArgumentList.Add("--disable-internal-agent");
        start.ArgumentList.Add(action.Program);
        foreach (var argument in argv) start.ArgumentList.Add(argument);

        var run = await PkexecRun.StartAsync(start, _host).ConfigureAwait(false);
        if (run is null)
            return new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        using var process = run.Process;
        using var pidfd = run.Pidfd;

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
            if (_host.TryStopBeforeStart(process, run.Pidfd))
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

    // pkexec clears the environment itself, but it first checks SHELL against /etc/shells and exits with 127 when
    // it is not listed, as a store path from a development shell is not. Only what pkexec passes on, minus SHELL.
    private static readonly string[] PassedVariables =
        ["LANG", "LANGUAGE", "LINGUAS", "LC_ALL", "LC_COLLATE", "LC_CTYPE", "LC_MESSAGES", "LC_MONETARY", "LC_NUMERIC", "LC_TIME", "TERM", "COLORTERM", "DISPLAY", "XAUTHORITY"];

    internal const string PkexecPath = "/usr/sbin:/usr/bin:/sbin:/bin";

    private static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        var kept = PassedVariables.Where(environment.ContainsKey).Select(name => (name, environment[name])).ToList();
        environment.Clear();
        foreach (var (name, value) in kept) environment[name] = value;
        environment["PATH"] = PkexecPath;
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
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            // pkexec selects the action by the program's real path and argv[1], but the privileged program sees only
            // argv[1], and one executable may be installed under several paths. So a verb names exactly one action,
            // and an action without a verb must be the only one (RUNICPA003 at build time).
            if (byId.Values.FirstOrDefault(a => a.Verb == action.Verb || a.Verb is null || action.Verb is null) is { } clash)
                throw new ArgumentException($"Actions '{clash.Id}' and '{action.Id}' cannot be told apart by their first argument; give each its own verb.", nameof(actions));
            if (!byId.TryAdd(action.Id, action)) throw new ArgumentException($"Action '{action.Id}' is declared twice.", nameof(actions));
        }
        return byId.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>pkexec, started from a thread that lives until it exits.</summary>
    /// <remarks>
    /// pkexec sets <c>PR_SET_PDEATHSIG</c> to SIGTERM, which Linux sends when the thread that forked it ends; a
    /// thread-pool thread can retire while the user reads the authentication dialog. The kernel clears the
    /// parent-death signal when pkexec changes its credentials to root's, so it guards only the authorization; once
    /// the program runs, the application's death reaches it only as the end of its standard input.
    /// </remarks>
    private sealed class PkexecRun
    {
        private PkexecRun(Process process, ProcessHandle pidfd, Task<int> exited) { Process = process; Pidfd = pidfd; Exited = exited; }
        public Process Process { get; }
        /// <summary>A pidfd opened right after the start, so a later signal cannot reach a reused process ID.</summary>
        public ProcessHandle Pidfd { get; }
        public Task<int> Exited { get; }

        public static Task<PkexecRun?> StartAsync(ProcessStartInfo start, IPolkitHost host)
        {
            var started = new TaskCompletionSource<PkexecRun?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                // An exception must not escape this thread: it would end the application.
                Process? process;
                try { process = Process.Start(start); }
                catch (System.ComponentModel.Win32Exception) { process = null; }
                catch (Exception e)
                {
                    started.SetException(e);
                    return;
                }
                if (process is null)
                {
                    started.SetResult(null);
                    return;
                }
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                started.SetResult(new PkexecRun(process, host.OpenProcess(process.Id), exited.Task));
                try
                {
                    process.WaitForExit();
                    exited.SetResult(process.ExitCode);
                }
                catch (Exception e) { exited.SetException(e); }
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
