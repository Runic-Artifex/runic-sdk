using System.Collections.Concurrent;
using System.Diagnostics;
using Runic.Platform;
using Runic.Platform.Linux.Polkit;
using TUnit.Core;
using static DeclarationTests;

/// <summary>The provider against a fake pkexec that plays the authority: it allows, refuses or fails as told.</summary>
internal sealed class PkexecTests
{
    // The messages and exit codes of polkit 127's pkexec (src/programs/pkexec.c).
    private const string FakePkexec = """
        #!/bin/sh
        # The provider passes a fixed PATH that may not have the test's tools; the fixture saved the test's own.
        dir=${0%/*}
        env > "$dir/environment"
        read -r PATH < "$dir/path"
        export PATH
        printf '%s\n' "$@" > "$dir/argv"
        [ "$1" = --disable-internal-agent ] && shift
        case "$(cat "$dir/decision")" in
          allow) exec "$@" ;;
          deny) printf 'Error executing command as another user: Not authorized\n\nThis incident has been reported.\n' >&2; exit 127 ;;
          dismiss) echo 'Error executing command as another user: Request dismissed' >&2; exit 126 ;;
          dismiss-silent) exit 126 ;;
          no-agent) echo 'Error executing command as another user: No authentication agent found.' >&2; exit 127 ;;
          no-terminal) echo "Error creating textual authentication agent: Error opening current controlling terminal for the process (\`/dev/tty'): No such device or address" >&2; exit 127 ;;
          no-polkit) echo "Error checking for authorization org.example.app.report: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable" >&2; exit 127 ;;
          not-setuid) echo 'pkexec must be setuid root' >&2; exit 127 ;;
          other) echo 'Error executing /missing: No such file or directory' >&2; exit 127 ;;
          pending) exec sleep 60 ;;
        esac
        """;

    private const string Report = """
        #!/bin/sh
        echo "verb=$1"
        shift
        for argument in "$@"; do printf 'arg <%s>\n' "$argument"; done
        echo 'from stderr' >&2
        printf 'no newline at the end'
        exit 3
        """;

    private const string Wait = """
        #!/bin/sh
        echo waiting
        cat > /dev/null
        echo 'stopped by caller'
        exit 5
        """;

    [Test, DisplayName("pkexec: an allowed action runs its argument vector without a shell and streams its output")]
    public async Task AllowedAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        string[] arguments = ["two words", "; touch injected", "$(id)", "`id`", "a'b\"c", "*", ""];
        var result = await fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, [.. arguments]) { Output = lines.Enqueue });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success(var completion) && completion.ExitCode == 3 && !completion.Succeeded, $"result {result}");
        var stdout = lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardOutput).Select(l => l.Text).ToList();
        Check(stdout.SequenceEqual(["verb=report", .. arguments.Select(a => $"arg <{a}>"), "no newline at the end"]), $"stdout: {string.Join(" | ", stdout)}");
        Check(lines.Any(l => l is { Channel: PrivilegedOutputChannel.StandardError, Text: "from stderr" }), "stderr");
        Check(!File.Exists(Path.Combine(fixture.Directory, "injected")) && !File.Exists("injected"), "no shell ran the arguments");
        Check(fixture.Argv.SequenceEqual(["--disable-internal-agent", fixture.ReportAction.Program, "report", .. arguments]), $"argv: {string.Join(" | ", fixture.Argv)}");
        var environment = fixture.Environment;
        Check(environment.GetValueOrDefault("PATH") == PolkitActionRunner.PkexecPath, $"pkexec gets a fixed PATH: {environment.GetValueOrDefault("PATH")}");
        Check(!environment.ContainsKey("SHELL") && !environment.ContainsKey("HOME") && !environment.ContainsKey("NUGET_PACKAGES"),
            $"pkexec gets no SHELL (it would reject one missing from /etc/shells) and nothing else of the caller's: {string.Join(",", environment.Keys)}");
        // The program may already have exited and been reaped when the pidfd is opened; CancelBeforeStartAsync checks the
        // pidfd of a pkexec that is still running.
        Check(fixture.Host.OpenRequested, "the provider opens a pidfd for pkexec");
    }

    [Test, DisplayName("pkexec: terminal authentication is opt-in")]
    public async Task TerminalAuthenticationAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow", new PolkitOptions { AllowTerminalAuthentication = true });
        Check(await fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, [])) is PlatformResult<PrivilegedActionCompletion>.Success, "ran");
        Check(fixture.Argv[0] == fixture.ReportAction.Program, $"argv: {string.Join(" | ", fixture.Argv)}");
    }

    [Test, DisplayName("pkexec: denied, dismissed, unavailable and failed outcomes are distinct")]
    [Arguments("deny", "denied")]
    [Arguments("dismiss", "dismissed")]
    [Arguments("dismiss-silent", "dismissed")]
    [Arguments("no-agent", "unavailable")]
    [Arguments("no-terminal", "unavailable")]
    [Arguments("no-polkit", "unavailable")]
    [Arguments("not-setuid", "unavailable")]
    [Arguments("other", "failed")]
    public async Task OutcomeAsync(string decision, string expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(decision);
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        var result = await fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, []) { Output = lines.Enqueue });
        var actual = result switch
        {
            PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Code: 127 } => "denied",
            PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.UserDismissed, Diagnostic.Code: 126 } => "dismissed",
            PlatformResult<PrivilegedActionCompletion>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable } => "unavailable",
            PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.Other, Diagnostic: { Domain: "pkexec", Code: 127 } } => "failed",
            _ => result.ToString(),
        };
        Check(actual == expected, $"{decision}: {result}");
        Check(lines.All(l => l.Channel == PrivilegedOutputChannel.StandardError), "pkexec's diagnostic arrives as error output");
    }

    [Test, DisplayName("pkexec: no setuid pkexec, or one that cannot start, is unavailable")]
    public async Task NoPkexecAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        var missing = new PolkitActionRunner([fixture.ReportAction], new PolkitOptions(), new FakeHost(null));
        Check(await missing.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, [])) is PlatformResult<PrivilegedActionCompletion>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "none found");
        var broken = new PolkitActionRunner([fixture.ReportAction], new PolkitOptions(), new FakeHost(Path.Combine(fixture.Directory, "no-such-pkexec")));
        Check(await broken.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, [])) is PlatformResult<PrivilegedActionCompletion>.Unavailable, "cannot start");
        Check(LinuxPolkitHost.Instance.FindPkexec(fixture.Pkexec) is null, "a pkexec that is not setuid root is not used");
    }

    [Test, DisplayName("pkexec: a program that is missing or replaceable is never started")]
    [Arguments(ProgramCheck.Missing, 1)]
    [Arguments(ProgramCheck.NotRealPath, 2)]
    [Arguments(ProgramCheck.Replaceable, 3)]
    public async Task UntrustedProgramAsync(ProgramCheck check, int code)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        fixture.Host.Check = check;
        var result = await fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, []));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic: { Domain: "Runic.PrivilegedAction" } d } && d.Code == code, $"result {result}");
        Check(!File.Exists(Path.Combine(fixture.Directory, "argv")), "pkexec did not run");
    }

    [Test, DisplayName("pkexec: requests outside the declarations are rejected before anything runs")]
    public async Task RejectedRequestsAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        var undeclared = new PrivilegedActionDeclaration("org.example.app.other", fixture.ReportAction.Program) { Verb = "other" };
        var lookalike = new PrivilegedActionDeclaration(fixture.ReportAction.Id, "/usr/bin/env") { Verb = "report", MaximumArguments = 8 };
        await ThrowsAsync<ArgumentException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(undeclared, [])).AsTask(), "undeclared");
        await ThrowsAsync<ArgumentException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(lookalike, [])).AsTask(), "same ID, other program");
        await ThrowsAsync<ArgumentException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, [.. Enumerable.Repeat("x", 9)])).AsTask(), "too many");
        await ThrowsAsync<ArgumentException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, default)).AsTask(), "default arguments");
        await ThrowsAsync<OperationCanceledException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, []), new CancellationToken(true)).AsTask(), "cancelled");
        Check(!File.Exists(Path.Combine(fixture.Directory, "argv")), "pkexec did not run");
    }

    [Test, DisplayName("pkexec: cancelling during authorization stops pkexec and throws")]
    public async Task CancelBeforeStartAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("pending");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, []), cancellation.Token).AsTask(), "cancelled");
        Check(watch.Elapsed < TimeSpan.FromSeconds(20), $"pkexec was stopped, not waited for: {watch.Elapsed}");
        Check(fixture.Host.OpenedProcess, "a pidfd was opened for the waiting pkexec");
    }

    [Test, DisplayName("pkexec: cancelling a program that runs as root closes its input and returns how it ended")]
    public async Task CancelRunningAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        fixture.Host.RunsAsRoot = true;
        using var cancellation = new CancellationTokenSource();
        var lines = new ConcurrentQueue<string>();
        var result = await fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.WaitAction, [])
        {
            Output = line =>
            {
                lines.Enqueue(line.Text);
                if (line.Text == "waiting") cancellation.Cancel();
            },
        }, cancellation.Token).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(30));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 5 }, $"result {result}");
        Check(lines.Contains("stopped by caller"), "the program saw its input close");
    }

    [Test, DisplayName("pkexec: an exception from the output callback is rethrown after the program exits")]
    public async Task CallbackFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture("allow");
        var calls = 0;
        await ThrowsAsync<FormatException>(() => fixture.Actions.RunAsync(new PrivilegedActionRequest(fixture.ReportAction, ["a", "b"])
        {
            Output = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new FormatException();
            },
        }).AsTask(), "rethrown");
        Check(calls == 1, $"no further calls after the failure: {calls}");
    }

    [Test, DisplayName("pkexec: classification of exit codes and diagnostics")]
    public void Classification()
    {
        Check(PkexecOutcome.Classify(0, "") is PlatformResult<PrivilegedActionCompletion>.Success { Value.Succeeded: true }, "0");
        Check(PkexecOutcome.Classify(1, "Error executing command as another user: Not authorized") is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 1 },
            "only 127 is read as a pkexec refusal");
        Check(PkexecOutcome.Classify(127, "program output\nError executing command as another user: Not authorized\n") is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "the marker may follow other lines");
        Check(PkexecOutcome.Classify(127, "  Error executing command as another user: Not authorized") is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.Other },
            "markers start a line");
        Check(PkexecOutcome.Classify(127, "Error registering local authentication agent: x") is PlatformResult<PrivilegedActionCompletion>.Unavailable, "agent registration");
        Check(PkexecOutcome.Classify(127, "Error getting authority: x") is PlatformResult<PrivilegedActionCompletion>.Unavailable, "authority");
        Check(PkexecOutcome.Classify(137, "") is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 137 }, "killed by a signal");
    }

    [Test, DisplayName("output: long lines arrive in parts without splitting a character, and invalid UTF-8 is replaced")]
    public async Task LongLinesAsync()
    {
        // A two-byte é straddles the buffer boundary, then a CRLF line, a line with an invalid byte, and a last line.
        var bytes = new List<byte>(Enumerable.Repeat((byte)'a', LineReader.MaximumLineBytes - 1));
        bytes.AddRange("étail\r\n"u8.ToArray());
        bytes.Add(0xFF);
        bytes.AddRange("-invalid\nlast"u8.ToArray());
        var lines = new List<string>();
        await LineReader.PumpAsync(new MemoryStream([.. bytes]), lines.Add);
        Check(lines.Count == 4, $"count {lines.Count}");
        Check(lines[0] == new string('a', LineReader.MaximumLineBytes - 1), "the first part stops before the character");
        Check(lines[1] == "étail", $"carriage return stripped: {lines[1]}");
        Check(lines[2] == "�-invalid", $"replacement: {lines[2]}");
        Check(lines[3] == "last", "a final line without a newline");
        Check(LineReader.CompleteUtf8Prefix([0x61, 0xE2, 0x82]) == 1 && LineReader.CompleteUtf8Prefix([0x61, 0xE2, 0x82, 0xAC]) == 4, "prefix");
    }

    internal sealed class FakeHost(string? pkexec) : IPolkitHost
    {
        public ProgramCheck Check { get; set; } = ProgramCheck.Trusted;
        // pkexec has switched to root: the caller can no longer signal it.
        public bool RunsAsRoot { get; set; }
        public bool OpenRequested { get; private set; }
        public bool OpenedProcess { get; private set; }
        public string? FindPkexec(string? configured) => pkexec;
        public ProgramCheck CheckProgram(string program) => Check;
        public ProcessHandle OpenProcess(int pid)
        {
            OpenRequested = true;
            var handle = LinuxPolkitHost.Instance.OpenProcess(pid);
            OpenedProcess |= handle.IsValid;
            return handle;
        }
        public bool TryStopBeforeStart(Process process, ProcessHandle pidfd) => !RunsAsRoot && LinuxPolkitHost.Instance.TryStopBeforeStart(process, pidfd);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string decision, PolkitOptions? options = null)
        {
            Directory = Path.Combine(Path.GetTempPath(), "runic-polkit-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(Directory);
            Pkexec = Script("pkexec", FakePkexec);
            File.WriteAllText(Path.Combine(Directory, "decision"), decision + "\n");
            File.WriteAllText(Path.Combine(Directory, "path"), System.Environment.GetEnvironmentVariable("PATH") + "\n");
            ReportAction = new PrivilegedActionDeclaration("org.example.app.report", Script("report", Report)) { Verb = "report", MaximumArguments = 8 };
            WaitAction = new PrivilegedActionDeclaration("org.example.app.wait", Script("wait", Wait)) { Verb = "wait" };
            Host = new FakeHost(Pkexec);
            Actions = new PolkitActionRunner([ReportAction, WaitAction], options ?? new PolkitOptions(), Host);
        }

        public string Directory { get; }
        public string Pkexec { get; }
        public PrivilegedActionDeclaration ReportAction { get; }
        public PrivilegedActionDeclaration WaitAction { get; }
        public FakeHost Host { get; }
        public PolkitActionRunner Actions { get; }
        public string[] Argv => File.ReadAllText(Path.Combine(Directory, "argv")).Split('\n')[..^1];
        public Dictionary<string, string> Environment => File.ReadAllLines(Path.Combine(Directory, "environment"))
            .Select(line => line.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

        private string Script(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, content.ReplaceLineEndings("\n") + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
