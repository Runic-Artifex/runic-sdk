using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Runic.Platform;
using Runic.Platform.Windows.Elevation;
using TUnit.Core;
using static Runic.Elevation.Smoke.Checks;

namespace Runic.Elevation.Smoke;

/// <summary>
/// The provider against a fake host: an in-process helper speaks the real protocol (<see cref="HelperChannel"/>) over a
/// socket pair, so every outcome, the output and each phase of cancellation run on every platform.
/// </summary>
internal sealed class SessionTests
{
    private static WindowsPrivilegedActions Provider(FakeHost host, WindowsElevationOptions? options = null) =>
        new(PrivilegedActions.All, options ?? PrivilegedActions.WindowsElevation, @"C:\Program Files\Runic Smoke", host);

    [Test, DisplayName("session: an accepted action streams both channels and reports its exit code")]
    public async Task RunsAsync()
    {
        var host = new FakeHost
        {
            Action = (channel, _) =>
            {
                channel.Write(ElevationProtocol.StandardOutput, Encoding.UTF8.GetBytes("first\r\nsec"));
                channel.Write(ElevationProtocol.StandardError, Encoding.UTF8.GetBytes("error line\n"));
                channel.Write(ElevationProtocol.StandardOutput, Encoding.UTF8.GetBytes("ond\n" + new string('y', 40000) + "\nno newline"));
                return 3;
            },
        };
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        var result = await Provider(host).RunAsync(new(PrivilegedActions.Report, [.. ProtocolTests.AwkwardArguments.Take(8)]) { Output = lines.Enqueue });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 3 }), $"result {result}");
        var stdout = lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardOutput).Select(l => l.Text).ToList();
        Check(stdout[0] == "first" && stdout[1] == "second" && stdout[^1] == "no newline" && string.Concat(stdout.Skip(2).SkipLast(1)) == new string('y', 40000), string.Join(" | ", stdout.Select(l => l.Length > 12 ? l[..12] : l)));
        Check(lines.Any(l => l is { Channel: PrivilegedOutputChannel.StandardError, Text: "error line" }), "stderr");
        Check(host.CheckedPath == @"C:\Program Files\Runic Smoke\Runic.Elevation.Smoke.exe", $"helper {host.CheckedPath}");
        var parsed = ProtocolTests.ParseLikeCrt(host.CommandLine!);
        Check(parsed[0] == ElevationProtocol.Marker && ElevationProtocol.IsPipeName(parsed[1]) && parsed[1] == host.Pipe!.Name
            && parsed.Skip(2).SequenceEqual(["report", .. ProtocolTests.AwkwardArguments.Take(8)]), $"command line {host.CommandLine}");
    }

    [Test, DisplayName("session: the prompt declined, UAC off, a rejected action, a JIT helper and a helper that never connects")]
    public async Task OutcomesAsync()
    {
        static Task<PlatformResult<PrivilegedActionCompletion>> Run(FakeHost host) => Provider(host).RunAsync(new(PrivilegedActions.Wait, [])).AsTask();
        var dismissed = new FakeHost { LaunchError = 1223 };
        Check(await Run(dismissed) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed, _) && dismissed.Pipe!.Disposed, "dismissed, pipe closed");
        var notElevated = new FakeHost { Status = HelperStatus.NotElevated };
        Check(await Run(notElevated) is PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable) && !notElevated.ActionStarted, "UAC off");
        var rejected = new FakeHost { Status = HelperStatus.Rejected };
        var rejectedResult = await Run(rejected);
        Check(rejectedResult is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 5 }) && !rejected.ActionStarted, $"rejected: {rejectedResult}");
        var jit = new FakeHost { Status = HelperStatus.NotNativeAot };
        Check(await Run(jit) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 4 }), "JIT helper");
        var silent = new FakeHost { ConnectHelper = false };
        Check(await Run(silent) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.Other, { Code: 7 }), "exited without connecting");
        var untrusted = new FakeHost { Check = ProgramCheck.Replaceable };
        Check(await Run(untrusted) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 3 }) && untrusted.Launches == 0, "untrusted, never prompted");
        var missing = new FakeHost { Check = ProgramCheck.Missing };
        Check(await Run(missing) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 1 }) && missing.Launches == 0, "missing, never prompted");
        await Throws<ArgumentException>(() => Provider(new FakeHost()).RunAsync(new(PrivilegedActions.Flood, ["x"])).AsTask(), "arguments outside the declaration");
        await Throws<ArgumentException>(() => Provider(new FakeHost()).RunAsync(new(new PrivilegedActionDeclaration("dev.runic.elevation-smoke.wait", "/opt/other") { Verb = "wait" }, [])).AsTask(), "another declaration with a known ID");
    }

    [Test, DisplayName("session: cancelled while the prompt is open, the helper approved later finds no pipe and is terminated")]
    public async Task CancelDuringPromptAsync()
    {
        var host = new FakeHost { LaunchGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cancellation = new CancellationTokenSource();
        var run = Provider(host).RunAsync(new(PrivilegedActions.Wait, []), cancellation.Token).AsTask();
        await host.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)), "cancellation does not wait for the prompt");
        Check(host.Pipe!.Disposed, "the pipe is closed at once");
        host.LaunchGate.SetResult();
        Check(!await host.HelperDone.Task.WaitAsync(TimeSpan.FromSeconds(10)), "a later approval runs nothing");
        await host.Process!.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Check(host.Process.TerminatedWith == WindowsPrivilegedActions.TerminatedExitCode || host.Process.Exited.Result == 2, "the late helper is terminated or exits on its own");
    }

    [Test, DisplayName("session: cancelled after the greeting and before the start signal, nothing runs")]
    public async Task CancelBeforeStartAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new FakeHost();
        host.BeforeHello = () => cancellation.Cancel();
        await Throws<OperationCanceledException>(() => Provider(host).RunAsync(new(PrivilegedActions.Wait, []), cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10)), "cancelled");
        Check(!await host.HelperDone.Task.WaitAsync(TimeSpan.FromSeconds(10)) && !host.ActionStarted, "the helper never got the start signal");
    }

    [Test, DisplayName("session: cancelling a running action asks it to stop and reports how it ended")]
    public async Task CancelRunningAsync()
    {
        var host = new FakeHost
        {
            Action = (channel, _) =>
            {
                channel.Write(ElevationProtocol.StandardOutput, "waiting\n"u8);
                channel.Cancelled.WaitHandle.WaitOne();
                channel.Write(ElevationProtocol.StandardOutput, "stopped by caller\n"u8);
                return 5;
            },
        };
        using var cancellation = new CancellationTokenSource();
        var lines = new ConcurrentQueue<string>();
        var result = await Provider(host).RunAsync(new(PrivilegedActions.Wait, [])
        {
            Output = line =>
            {
                lines.Enqueue(line.Text);
                if (line.Text == "waiting") cancellation.Cancel();
            },
        }, cancellation.Token);
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 5 }), $"result {result}");
        Check(lines.SequenceEqual(["waiting", "stopped by caller"]), string.Join(" | ", lines));
        Check(host.Process!.TerminatedWith is null, "not terminated without TerminateAfterCancellation");
    }

    [Test, DisplayName("session: an action that ignores cancellation is terminated after TerminateAfterCancellation")]
    public async Task TerminateAfterCancellationAsync()
    {
        var host = new FakeHost
        {
            Action = (channel, process) =>
            {
                channel.Write(ElevationProtocol.StandardOutput, "ignoring\n"u8);
                process.Exited.Wait(TimeSpan.FromSeconds(30));
                return 6;
            },
        };
        using var cancellation = new CancellationTokenSource();
        var options = PrivilegedActions.WindowsElevation with { TerminateAfterCancellation = TimeSpan.FromMilliseconds(200) };
        var result = await Provider(host, options).RunAsync(new(PrivilegedActions.Ignore, []) { Output = line => { if (line.Text == "ignoring") cancellation.Cancel(); } }, cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: WindowsPrivilegedActions.TerminatedExitCode }), $"result {result}");
        Check(host.Process!.TerminatedWith == WindowsPrivilegedActions.TerminatedExitCode, "terminated through the handle");
    }

    [Test, DisplayName("session: a cancelled action that cannot be terminated is reported instead of waited for")]
    public async Task NotTerminatedAsync()
    {
        var host = new FakeHost
        {
            TerminateFails = true,
            Action = (channel, process) =>
            {
                channel.Write(ElevationProtocol.StandardOutput, "ignoring\n"u8);
                process.Exited.Wait(TimeSpan.FromSeconds(30));
                return 6;
            },
        };
        using var cancellation = new CancellationTokenSource();
        var options = PrivilegedActions.WindowsElevation with { TerminateAfterCancellation = TimeSpan.FromMilliseconds(200) };
        var result = await Provider(host, options).RunAsync(new(PrivilegedActions.Ignore, []) { Output = line => { if (line.Text == "ignoring") cancellation.Cancel(); } }, cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, { Code: 8 }), $"result {result}");
        host.Process!.Exit(6);
    }

    [Test, DisplayName("session: a slow callback holds the helper's writes, and every line arrives in order")]
    public async Task BackpressureAsync()
    {
        const int Lines = 4000;
        var written = 0;
        var host = new FakeHost
        {
            Action = (channel, _) =>
            {
                for (var i = 0; i < Lines; i++)
                {
                    channel.Write(ElevationProtocol.StandardOutput, Encoding.UTF8.GetBytes($"line {i} {new string('z', 1000)}\n"));
                    Volatile.Write(ref written, i + 1);
                }
                return 0;
            },
        };
        using var gate = new ManualResetEventSlim();
        var received = new List<string>();
        var run = Provider(host).RunAsync(new(PrivilegedActions.Flood, ["1"])
        {
            Output = line =>
            {
                gate.Wait();
                received.Add(line.Text);
            },
        }).AsTask();
        await Task.Delay(500);
        var whileBlocked = Volatile.Read(ref written);
        Check(whileBlocked < Lines, $"the helper wrote {whileBlocked} of {Lines} lines while the callback blocked");
        gate.Set();
        Check(await run.WaitAsync(TimeSpan.FromSeconds(30)) is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 0 }), "ran");
        Check(received.Count == Lines && received.Select((l, i) => l.StartsWith($"line {i} ", StringComparison.Ordinal)).All(x => x), $"{received.Count} lines in order");
    }

    [Test, DisplayName("session: an exception from the output callback is thrown after the action ended")]
    public async Task CallbackFailureAsync()
    {
        var host = new FakeHost { Action = (channel, _) => { channel.Write(ElevationProtocol.StandardOutput, "one\ntwo\n"u8); return 0; } };
        await Throws<FormatException>(() => Provider(host).RunAsync(new(PrivilegedActions.Report, []) { Output = _ => throw new FormatException() }).AsTask(), "callback failure");
        Check(host.Process!.Exited.IsCompleted, "after the action ended");
    }

    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}: {message}");
    }
}

/// <summary>A connected socket pair: a full-duplex byte stream like the pipe, with real end-of-stream and cancellation.</summary>
internal static class SocketPair
{
    public static (Stream Caller, Stream Helper) Create()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Small buffers make the backpressure visible.
        client.SendBufferSize = client.ReceiveBufferSize = 64 * 1024;
        client.NoDelay = true;
        client.Connect(listener.LocalEndPoint!);
        var server = listener.Accept();
        server.SendBufferSize = server.ReceiveBufferSize = 64 * 1024;
        server.NoDelay = true;
        return (new NetworkStream(server, ownsSocket: true), new NetworkStream(client, ownsSocket: true));
    }
}

internal sealed class FakeHost : IElevationHost
{
    public ProgramCheck Check { get; init; } = ProgramCheck.Trusted;
    public int LaunchError { get; init; }
    public TaskCompletionSource? LaunchGate { get; init; }
    public HelperStatus Status { get; init; } = HelperStatus.Accepted;
    public bool ConnectHelper { get; init; } = true;
    public bool TerminateFails { get; init; }
    public Func<HelperChannel, FakeProcess, int>? Action { get; init; }
    public Action? BeforeHello { get; set; }

    public string? CheckedPath { get; private set; }
    public string? CommandLine { get; private set; }
    public FakePipe? Pipe { get; private set; }
    public FakeProcess? Process { get; private set; }
    public int Launches { get; private set; }
    public bool ActionStarted { get; private set; }
    public TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>True when the action started, false when the helper exited without starting it.</summary>
    public TaskCompletionSource<bool> HelperDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProgramCheck CheckProgram(string path)
    {
        CheckedPath = path;
        return Check;
    }

    public IElevationPipe CreatePipe(string name) => Pipe = new FakePipe(name);

    public async Task<LaunchResult> LaunchAsync(string program, string commandLine, nint ownerWindow)
    {
        Launches++;
        CommandLine = commandLine;
        Launched.TrySetResult();
        if (LaunchGate is { } gate) await gate.Task.ConfigureAwait(false);
        if (LaunchError != 0) return new(null, LaunchError);
        var process = Process = new FakeProcess { TerminateFails = TerminateFails };
        var pipe = Pipe!;
        _ = Task.Run(() => RunHelper(pipe, process));
        return new(process, 0);
    }

    private void RunHelper(FakePipe pipe, FakeProcess process)
    {
        if (!ConnectHelper || pipe.Connect() is not { } stream)
        {
            HelperDone.TrySetResult(false);
            process.Exit(2);
            return;
        }
        var channel = new HelperChannel(stream);
        process.OnTerminate = channel.Dispose;
        BeforeHello?.Invoke();
        var started = false;
        try
        {
            channel.SendHello(Status);
            started = Status == HelperStatus.Accepted && channel.WaitForGoAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (!started)
        {
            channel.Dispose();
            HelperDone.TrySetResult(false);
            process.Exit(2);
            return;
        }
        channel.StartWatching();
        ActionStarted = true;
        HelperDone.TrySetResult(true);
        var code = Action?.Invoke(channel, process) ?? 0;
        // A process's handles close when it exits.
        channel.Dispose();
        process.Exit(code);
    }
}

internal sealed class FakePipe(string name) : IElevationPipe
{
    private readonly Lock _lock = new();
    private readonly (Stream Caller, Stream Helper) _streams = SocketPair.Create();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _helperOwned;

    public string Name { get; } = name;
    public bool Disposed { get; private set; }

    /// <summary>The helper's end, or null when the application closed the pipe first.</summary>
    public Stream? Connect()
    {
        lock (_lock)
        {
            if (Disposed) return null;
            _helperOwned = true;
            _connected.TrySetResult();
            return _streams.Helper;
        }
    }

    public async Task<Stream?> AcceptAsync(int clientProcessId, Task processExited, CancellationToken cancellationToken)
    {
        await Task.WhenAny(_connected.Task, processExited).WaitAsync(cancellationToken).ConfigureAwait(false);
        return _connected.Task.IsCompleted && clientProcessId == FakeProcess.ProcessId ? _streams.Caller : null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Disposed = true;
            _streams.Caller.Dispose();
            if (!_helperOwned) _streams.Helper.Dispose();
        }
    }
}

internal sealed class FakeProcess : IHelperProcess
{
    public const int ProcessId = 4242;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id => ProcessId;
    public Task<int> Exited => _exited.Task;
    public int? TerminatedWith { get; private set; }
    public Action? OnTerminate { get; set; }
    public bool TerminateFails { get; init; }

    public void Exit(int code) => _exited.TrySetResult(code);

    public bool TryTerminate(int exitCode)
    {
        if (_exited.Task.IsCompleted || TerminateFails) return false;
        TerminatedWith = exitCode;
        OnTerminate?.Invoke();
        return _exited.TrySetResult(exitCode);
    }

    public void Dispose() { }
}
