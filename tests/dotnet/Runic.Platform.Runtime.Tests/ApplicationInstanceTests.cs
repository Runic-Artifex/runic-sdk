using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

internal sealed class ApplicationInstanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static string Directory() => Path.Combine(Path.GetTempPath(), "runic-instance-" + Guid.NewGuid().ToString("N")[..8]);

    [Test, DisplayName("INSTANCE: a second launch reaches the running instance with its arguments, directory and token")]
    public async Task ForwardsLaunchesAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            Check(first is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "the first launch is the running instance");
            await using var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
            var launches = Channel.CreateUnbounded<ApplicationLaunch>();
            instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);

            var launch = new ApplicationLaunch(["--open", "dashboard", "ümlaut", ""], "/home/alice") { PlatformContext = new DesktopActivationContext("token-1") };
            var second = await ApplicationInstance.ClaimAsync("org.example.App", launch, directory, default);
            Check(second is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "the second launch is forwarded");
            var received = await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(Wait);
            Check(received.Arguments.SequenceEqual(launch.Arguments) && received.WorkingDirectory == "/home/alice"
                && received.PlatformContext?.ActivationToken == "token-1" && received.PlatformContext.StartupId is null, "arguments, directory and token arrive");

            var other = await ApplicationInstance.ClaimAsync("org.example.Other", new ApplicationLaunch([], "/"), directory, default);
            Check(other is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "another application has its own instance");
            await ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)other).Value).Instance.DisposeAsync();

            for (int i = 0; i < 3; i++)
                Check(await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([i.ToString(System.Globalization.CultureInfo.InvariantCulture)], "/"), directory, default)
                    is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "later launches are forwarded too");
            for (int i = 0; i < 3; i++)
                Check((await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(Wait)).Arguments.Single() == i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "launches arrive one at a time, in order");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: ending the running instance, or a stale socket, lets the next launch take over")]
    public async Task TakesOverAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            await ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance.DisposeAsync();
            Check(System.IO.Directory.GetFiles(directory, "*.sock").Length == 0, "disposal removes the socket");

            // A crashed instance leaves its socket behind but not its lock.
            foreach (var lockFile in System.IO.Directory.GetFiles(directory, "*.lock"))
                File.WriteAllText(Path.ChangeExtension(lockFile, ".sock"), "");
            var next = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            Check(next is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "a stale socket does not block the next instance");
            await ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)next).Value).Instance.DisposeAsync();
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a lock holder that never answers is busy, not forwarded")]
    public async Task BusyAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
            string lockPath = System.IO.Directory.GetFiles(directory, "*.lock").Single();
            await instance.DisposeAsync();
            using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var busy = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            Check(busy is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.ResourceBusy }, "no answer within the timeout is busy");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a launch during start-up waits for the first handler")]
    public async Task WaitsForTheFirstHandlerAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            await using var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
            var second = ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["early"], "/"), directory, default).AsTask();
            await Task.Delay(500);
            Check(!second.IsCompleted, "a launch nobody received is not reported forwarded");
            var launches = Channel.CreateUnbounded<ApplicationLaunch>();
            instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);
            Check(await second.WaitScaledAsync(Wait) is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "the waiting launch is forwarded");
            Check((await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(Wait)).Arguments.Single() == "early", "the first handler receives it");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a launch nobody handles any more is not reported forwarded")]
    public async Task HandlerRemovedAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            await using var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
            EventHandler<ApplicationLaunch> handler = (_, _) => { };
            instance.Launched += handler;
            instance.Launched -= handler;
            var second = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(15));
            Check(second is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.ResourceBusy }, "the launch is busy, not lost");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a launch while the running instance ends becomes the next instance")]
    public async Task RelaunchDuringShutdownAsync()
    {
        string directory = Directory();
        try
        {
            var first = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
            // Nobody handles launches: the instance is quitting.
            var second = ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default).AsTask();
            await Task.Delay(300);
            await instance.DisposeAsync();
            var next = await second.WaitScaledAsync(Wait);
            Check(next is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "the relaunch takes over");
            await ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)next).Value).Instance.DisposeAsync();
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a directory too long for a socket is unavailable")]
    public async Task LongDirectoryAsync()
    {
        string directory = Path.Combine(Directory(), new string('d', 120));
        var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
        Check(claim is PlatformResult<ApplicationInstanceClaim>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "a long path is unavailable, not an exception");
        Check(!System.IO.Directory.Exists(directory), "nothing is created");
    }

    // The launching process grants the running instance, here another process, the foreground through the socket's
    // peer process ID. Two processes, so the instance's ID cannot be confused with the launching process's own.
    [Test, DisplayName("INSTANCE: a forwarded launch lets the running instance in another process take the foreground")]
    public async Task GrantsTheRunningInstanceTheForegroundAsync()
    {
        string directory = Directory();
        var granted = new List<(int ProcessId, bool Allowed)>();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Runtime.Tests.dll"));
        start.ArgumentList.Add("--instance-primary");
        start.ArgumentList.Add(directory);
        using var primary = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The running instance did not start.");
        ForegroundRights.Granted = (processId, allowed) => { lock (granted) granted.Add((processId, allowed)); };
        try
        {
            Check(await primary.StandardOutput.ReadLineAsync().WaitScaledAsync(Wait) == "ready", "the other process is the running instance");
            var claim = await ApplicationInstance.ClaimAsync("org.example.Peer", new ApplicationLaunch(["--show"], "/"), directory, default);
            Check(claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "the launch is forwarded");
            Check(await primary.StandardOutput.ReadLineAsync().WaitScaledAsync(Wait) == "launched --show", "the running instance receives it");
            await primary.WaitForExitAsync().WaitScaledAsync(Wait);
            if (OperatingSystem.IsWindows())
            {
                Check(granted is [{ ProcessId: var id }] && id == primary.Id,
                    $"AllowSetForegroundWindow was called for the running instance {primary.Id}, not [{string.Join(", ", granted)}]");
                Console.WriteLine($"AllowSetForegroundWindow({primary.Id}) {(granted[0].Allowed ? "succeeded" : "was refused: this process does not have the foreground")}");
            }
            else Check(granted.Count == 0, "only Windows grants the foreground");
        }
        finally
        {
            ForegroundRights.Granted = null;
            if (!primary.HasExited) primary.Kill();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    // The child process's side: become the running instance, report one launch and end.
    internal static async Task<int> RunPrimaryAsync(string directory)
    {
        var claim = await ApplicationInstance.ClaimAsync("org.example.Peer", new ApplicationLaunch([], "/"), directory, default);
        if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } }) return 1;
        await using (instance)
        {
            var launched = new TaskCompletionSource<ApplicationLaunch>(TaskCreationOptions.RunContinuationsAsynchronously);
            instance.Launched += (_, launch) => launched.TrySetResult(launch);
            Console.WriteLine("ready");
            var launch = await launched.Task.WaitScaledAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("launched " + string.Join(' ', launch.Arguments));
        }
        return 0;
    }

    private static readonly ApplicationInstanceOptions Replies = new() { ReplyToLaunches = true };

    private static async Task<IApplicationInstance> ClaimPrimaryAsync(string directory, ApplicationInstanceOptions options)
    {
        var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), options, directory, default);
        return claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } }
            ? instance : throw new InvalidOperationException($"the first launch is the running instance, not {claim}");
    }

    private static async Task<IForwardedLaunchReply> ForwardAsync(string directory, params string[] arguments)
    {
        var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([.. arguments], "/"), Replies, directory, default);
        return claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: { } reply } }
            ? reply : throw new InvalidOperationException($"the launch is forwarded with a reply, not {claim}");
    }

    private static async Task<List<string>> ReadAllAsync(IForwardedLaunchReply reply)
    {
        var messages = new List<string>();
        await foreach (var message in reply.ReadMessagesAsync()) messages.Add(message);
        return messages;
    }

    [Test, DisplayName("INSTANCE: the running instance replies to a forwarded launch with messages and an exit status")]
    public async Task RepliesAsync()
    {
        string directory = Directory();
        try
        {
            await using var instance = await ClaimPrimaryAsync(directory, Replies);
            var misuse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            instance.Launched += (_, launch) => _ = Task.Run(async () =>
            {
                var reply = launch.Reply ?? throw new InvalidOperationException("a launch that waits carries a reply");
                await reply.WriteAsync("hello " + launch.Arguments.Single());
                await reply.WriteAsync("ümlaut ✓");
                await reply.WriteAsync("");
                await reply.CompleteAsync(3);
                try { await reply.WriteAsync("late"); misuse.TrySetResult("a write after completion was sent"); }
                catch (InvalidOperationException)
                {
                    try { await reply.CompleteAsync(4); misuse.TrySetResult("a second exit status was sent"); }
                    catch (InvalidOperationException) { misuse.TrySetResult("refused"); }
                }
            });

            await using var reply = await ForwardAsync(directory, "world");
            var messages = await ReadAllAsync(reply).WaitScaledAsync(Wait);
            Check(messages.SequenceEqual(["hello world", "ümlaut ✓", ""]), "the messages arrive in order: " + string.Join(" | ", messages));
            Check(await reply.WaitForExitAsync().AsTask().WaitScaledAsync(Wait) is PlatformResult<int>.Success(3), "the exit status arrives");
            Check((await ReadAllAsync(reply)).Count == 0 && await reply.WaitForExitAsync() is PlatformResult<int>.Success(3), "the ended reply keeps its exit status");
            Check(await misuse.Task.WaitScaledAsync(Wait) == "refused", "nothing is sent after the exit status");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: exit statuses arrive, and waiting for one skips the messages not read")]
    public async Task ExitStatusesAsync()
    {
        string directory = Directory();
        try
        {
            await using var instance = await ClaimPrimaryAsync(directory, Replies);
            instance.Launched += (_, launch) => _ = Task.Run(async () =>
            {
                int code = int.Parse(launch.Arguments.Single(), System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < 100; i++) await launch.Reply!.WriteAsync("skipped");
                await launch.Reply!.CompleteAsync(code);
            });
            foreach (int code in new[] { 0, 1, 255, -1, int.MinValue })
            {
                await using var reply = await ForwardAsync(directory, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var exit = await reply.WaitForExitAsync().AsTask().WaitScaledAsync(Wait);
                Check(exit is PlatformResult<int>.Success(var received) && received == code, $"exit status {code}, not {exit}");
            }
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: many replies stream in order, and a reader that does not read slows the writer")]
    public async Task StreamsManyMessagesAsync()
    {
        const int Count = 10_000;
        string directory = Directory();
        try
        {
            await using var instance = await ClaimPrimaryAsync(directory, Replies);
            int written = 0;
            var sizes = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string padding = new('x', 1024);
            instance.Launched += (_, launch) => _ = Task.Run(async () =>
            {
                var reply = launch.Reply!;
                for (int i = 0; i < Count; i++)
                {
                    if (!await reply.WriteAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + padding)) return;
                    Volatile.Write(ref written, i + 1);
                }
                await reply.WriteAsync(new string('m', 1024 * 1024));
                try { await reply.WriteAsync(new string('m', 1024 * 1024 + 1)); sizes.TrySetResult("a message over 1 MiB was sent"); }
                catch (ArgumentException) { sizes.TrySetResult("refused"); }
                await reply.CompleteAsync(0);
            });

            await using var reply = await ForwardAsync(directory);
            await Task.Delay(500);
            int before = Volatile.Read(ref written);
            Check(before < Count, $"the writer waits for the reader instead of buffering all {Count} messages ({before} written)");
            int index = 0;
            await foreach (var message in reply.ReadMessagesAsync())
            {
                if (index < Count)
                    Check(message == index.ToString(System.Globalization.CultureInfo.InvariantCulture) + padding, $"message {index} arrives in order");
                else Check(message.Length == 1024 * 1024, "a message of 1 MiB arrives");
                index++;
            }
            Check(index == Count + 1, $"every message arrives ({index})");
            Check(await reply.WaitForExitAsync() is PlatformResult<int>.Success(0), "the exit status follows the messages");
            Check(await sizes.Task.WaitScaledAsync(Wait) == "refused", "a message over 1 MiB is refused");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: cancelling a forwarded launch, or leaving, cancels it in the running instance")]
    public async Task CancelsAsync()
    {
        string directory = Directory();
        try
        {
            await using var instance = await ClaimPrimaryAsync(directory, Replies);
            var leftWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reasons = new TaskCompletionSource<(bool CancelRequested, bool Disconnected, bool SeenFirst)>(TaskCreationOptions.RunContinuationsAsynchronously);
            instance.Launched += (_, launch) => _ = Task.Run(async () =>
            {
                var reply = launch.Reply!;
                // #309: the reason is cancelled before Cancelled, so a callback on Cancelled can tell which it was.
                var seenFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = reply.Cancelled.Register(() =>
                    seenFirst.TrySetResult(reply.CancelRequested.IsCancellationRequested || reply.Disconnected.IsCancellationRequested));
                try
                {
                    await reply.WriteAsync("started");
                    try { await Within(Task.Delay(Timeout.Infinite, reply.Cancelled), "the running instance sees the launch cancelled or left"); }
                    catch (OperationCanceledException) when (reply.Cancelled.IsCancellationRequested) { }
                    reasons.TrySetResult((reply.CancelRequested.IsCancellationRequested, reply.Disconnected.IsCancellationRequested,
                        await Within(seenFirst.Task, "the reason is cancelled with Cancelled")));
                    if (launch.Arguments.Single() == "cancel")
                    {
                        await reply.WriteAsync("cancelled");
                        await reply.CompleteAsync(130);
                    }
                    else leftWrite.TrySetResult(await reply.WriteAsync("nobody reads this"));
                }
                // The test reports what the running instance waited for, rather than timing out on its own wait.
                catch (Exception error)
                {
                    reasons.TrySetException(error);
                    leftWrite.TrySetException(error);
                }
            });

            // Ctrl+C: ask the running instance to cancel, and still read its last messages and exit status.
            await using (var reply = await ForwardAsync(directory, "cancel"))
            {
                await using var messages = reply.ReadMessagesAsync().GetAsyncEnumerator();
                Check(await Within(messages.MoveNextAsync().AsTask(), "the first message") && messages.Current == "started", "the launch starts");
                await reply.CancelAsync();
                await reply.CancelAsync();
                Check(await Within(messages.MoveNextAsync().AsTask(), "the message after the cancel request") && messages.Current == "cancelled", "the running instance sees the cancel request");
                Check(!await Within(messages.MoveNextAsync().AsTask(), "the end of the messages"), "the messages end at the exit status");
                Check(await Within(reply.WaitForExitAsync().AsTask(), "the exit status after cancelling") is PlatformResult<int>.Success(130), "the exit status after cancelling arrives");
                Check(await Within(reasons.Task, "the reasons after a cancel request") == (true, false, true), $"a cancel request is CancelRequested, not Disconnected: {reasons.Task.Result}");
            }

            // Leaving: disposing the reply, or cancelling a read, closes the connection.
            foreach (bool dispose in new[] { true, false })
            {
                leftWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                reasons = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var reply = await ForwardAsync(directory, "leave");
                using var stop = new CancellationTokenSource();
                await using (var messages = reply.ReadMessagesAsync(stop.Token).GetAsyncEnumerator())
                {
                    Check(await Within(messages.MoveNextAsync().AsTask(), $"the first message (dispose: {dispose})") && messages.Current == "started", "the launch starts");
                    if (dispose) await reply.DisposeAsync();
                    else
                    {
                        var next = messages.MoveNextAsync().AsTask();
                        await stop.CancelAsync();
                        try { await Within(next, "the cancelled read ends"); throw new InvalidOperationException("a cancelled read ends"); }
                        catch (OperationCanceledException) { }
                    }
                }
                Check(!await Within(leftWrite.Task, $"the running instance's write after leaving (dispose: {dispose})"), "the running instance is cancelled, and its writes are no longer sent");
                Check(await Within(reasons.Task, $"the reasons after leaving (dispose: {dispose})") == (false, true, true), $"leaving is Disconnected, not CancelRequested (dispose: {dispose}): {reasons.Task.Result}");
                Check(await Within(reply.WaitForExitAsync().AsTask(), "the result of a reply left early") is PlatformResult<int>.Failed { Code: PlatformFailureCode.IOError }, "a reply left early has no exit status");
            }
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    // #400: on Windows, a receive posted while the other end closes may never complete. The running instance missed
    // one in 50 to 200 launches that left right after their first message (under JIT and NativeAOT, on x64 and
    // arm64), and its later writes still succeeded.
    [Test, DisplayName("INSTANCE: the running instance sees a launch leave, however soon it leaves")]
    public async Task SeesEarlyLeavesAsync()
    {
        const int Launches = 500;
        string directory = Directory();
        try
        {
            await using var instance = await ClaimPrimaryAsync(directory, Replies);
            var seen = Channel.CreateUnbounded<Task<bool>>();
            instance.Launched += (_, launch) =>
            {
                var reply = launch.Reply!;
                seen.Writer.TryWrite(Task.Run(async () =>
                {
                    if (launch.Arguments.Single() == "after a message") await reply.WriteAsync("started");
                    // Disposing the instance ends a reply it never saw leave, so this wait ends with the test.
                    try { await Task.Delay(Timeout.Infinite, reply.Cancelled); }
                    catch (OperationCanceledException) { }
                    return reply.Disconnected.IsCancellationRequested;
                }));
            };
            foreach (var when in new[] { "after a message", "at once" })
                for (int i = 0; i < Launches; i++)
                {
                    await using (var reply = await ForwardAsync(directory, when))
                    {
                        if (when == "after a message")
                        {
                            await using var messages = reply.ReadMessagesAsync().GetAsyncEnumerator();
                            Check(await Within(messages.MoveNextAsync().AsTask(), $"the first message ({i})") && messages.Current == "started", "the launch starts");
                        }
                    }
                    var handled = await Within(seen.Reader.ReadAsync().AsTask(), $"the launch ({i})");
                    Check(await Within(handled, $"the running instance sees the launch leave {when} (launch {i} of {Launches})"),
                        $"leaving {when} is Disconnected (launch {i})");
                }
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    // #308: a command line that only talks to a running daemon never becomes the instance itself.
    [Test, DisplayName("INSTANCE: a forward-only launch reaches a running instance and never becomes one")]
    public async Task ForwardOnlyAsync()
    {
        string directory = Directory();
        var forwardOnly = Replies with { ForwardOnly = true };
        try
        {
            foreach (var options in new[] { forwardOnly, new ApplicationInstanceOptions { ForwardOnly = true } })
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var none = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["status"], "/"), options, directory, default).AsTask().WaitScaledAsync(Wait);
                Check(none is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.NotRunning }, $"without an instance it is not running, not {none}");
                Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"and reported at once ({stopwatch.Elapsed})");
            }
            Check(System.IO.Directory.GetFiles(directory, "*.sock").Length == 0, "nothing listens for launches");

            // The next launch becomes the instance at once: the forward-only launch did not keep the lock.
            var stopwatch2 = System.Diagnostics.Stopwatch.StartNew();
            await using (var instance = await ClaimPrimaryAsync(directory, Replies))
            {
                Check(stopwatch2.Elapsed < TimeSpan.FromSeconds(1), $"the next launch is the instance at once ({stopwatch2.Elapsed})");
                instance.Launched += (_, launch) => _ = Task.Run(async () =>
                {
                    if (launch.Reply is not { } reply) return;
                    await reply.WriteAsync("from the instance");
                    await reply.CompleteAsync(7);
                });
                var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["status"], "/"), forwardOnly, directory, default);
                Check(claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: { } reply } }, $"with an instance it is forwarded, not {claim}");
                await using var forwarded = ((ApplicationInstanceClaim.Forwarded)((PlatformResult<ApplicationInstanceClaim>.Success)claim).Value).Reply!;
                Check((await ReadAllAsync(forwarded).WaitScaledAsync(Wait)).SequenceEqual(["from the instance"]) && await forwarded.WaitForExitAsync() is PlatformResult<int>.Success(7),
                    "and the reply arrives");
            }

            // An instance that is starting, which holds the lock but has no handler yet, is waited for.
            await using (var starting = await ClaimPrimaryAsync(directory, new ApplicationInstanceOptions()))
            {
                var claim = ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["early"], "/"), new ApplicationInstanceOptions { ForwardOnly = true }, directory, default).AsTask();
                await Task.Delay(300);
                Check(!claim.IsCompleted, "a forward-only launch waits for a starting instance");
                var launches = Channel.CreateUnbounded<ApplicationLaunch>();
                starting.Launched += (_, launch) => launches.Writer.TryWrite(launch);
                Check(await claim.WaitScaledAsync(Wait) is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: null } }, "and is forwarded to it");
                Check((await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(Wait)).Arguments.Single() == "early", "which receives it");
            }

            // An instance that ends while the launch waits: not running, rather than taking over.
            var ending = await ClaimPrimaryAsync(directory, new ApplicationInstanceOptions());
            var late = ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), new ApplicationInstanceOptions { ForwardOnly = true }, directory, default).AsTask();
            await Task.Delay(300);
            await ending.DisposeAsync();
            Check(await late.WaitScaledAsync(Wait) is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.NotRunning }, "an instance that ended is not running");
            await (await ClaimPrimaryAsync(directory, new ApplicationInstanceOptions())).DisposeAsync();
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a running instance that ends mid-reply is reported, not waited for")]
    public async Task PrimaryEndsAsync()
    {
        string directory = Directory();
        try
        {
            var instance = await ClaimPrimaryAsync(directory, Replies);
            var after = new TaskCompletionSource<(bool Cancelled, bool Disconnected, bool CancelRequested, bool Sent)>(TaskCreationOptions.RunContinuationsAsynchronously);
            instance.Launched += (_, launch) => _ = Task.Run(async () =>
            {
                var reply = launch.Reply!;
                await reply.WriteAsync("one");
                try { await Task.Delay(Timeout.Infinite, reply.Cancelled).WaitScaledAsync(Wait); }
                catch (OperationCanceledException) when (reply.Cancelled.IsCancellationRequested) { }
                after.TrySetResult((reply.Cancelled.IsCancellationRequested, reply.Disconnected.IsCancellationRequested, reply.CancelRequested.IsCancellationRequested,
                    await reply.WriteAsync("two")));
            });
            await using var reply = await ForwardAsync(directory);
            await using var messages = reply.ReadMessagesAsync().GetAsyncEnumerator();
            Check(await messages.MoveNextAsync().AsTask().WaitScaledAsync(Wait) && messages.Current == "one", "the reply starts");
            await instance.DisposeAsync();
            Check(!await messages.MoveNextAsync().AsTask().WaitScaledAsync(Wait), "the messages end with the running instance");
            Check(await reply.WaitForExitAsync().AsTask().WaitScaledAsync(Wait) is PlatformResult<int>.Failed { Code: PlatformFailureCode.IOError }, "no exit status is an I/O failure");
            Check(await after.Task.WaitScaledAsync(Wait) == (true, true, false, false), "ending the instance cancels the reply as Disconnected");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: a running instance in another process that crashes mid-reply is reported")]
    public async Task PrimaryCrashesAsync()
    {
        string directory = Directory();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Runtime.Tests.dll"));
        start.ArgumentList.Add("--instance-replier");
        start.ArgumentList.Add(directory);
        using var primary = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The running instance did not start.");
        try
        {
            Check(await primary.StandardOutput.ReadLineAsync().WaitScaledAsync(Wait) == "ready", "the other process is the running instance");
            var claim = await ApplicationInstance.ClaimAsync("org.example.Replier", new ApplicationLaunch([], "/"), Replies, directory, default);
            Check(claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: not null } }, "the launch is forwarded with a reply");
            await using var reply = ((ApplicationInstanceClaim.Forwarded)((PlatformResult<ApplicationInstanceClaim>.Success)claim).Value).Reply!;
            await using var messages = reply.ReadMessagesAsync().GetAsyncEnumerator();
            Check(await messages.MoveNextAsync().AsTask().WaitScaledAsync(Wait) && messages.Current == "running", "the reply starts");
            primary.Kill();
            Check(!await messages.MoveNextAsync().AsTask().WaitScaledAsync(Wait), "the messages end with the crashed process");
            Check(await reply.WaitForExitAsync().AsTask().WaitScaledAsync(Wait) is PlatformResult<int>.Failed { Code: PlatformFailureCode.IOError }, "no exit status is an I/O failure");
        }
        finally
        {
            if (!primary.HasExited) primary.Kill();
            await primary.WaitForExitAsync();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    // The child process's side: become the running instance, start a reply and never finish it.
    internal static async Task<int> RunReplierAsync(string directory)
    {
        var claim = await ApplicationInstance.ClaimAsync("org.example.Replier", new ApplicationLaunch([], "/"), Replies, directory, default);
        if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } }) return 1;
        await using (instance)
        {
            instance.Launched += (_, launch) => _ = launch.Reply?.WriteAsync("running").AsTask();
            Console.WriteLine("ready");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
        return 0;
    }

    [Test, DisplayName("INSTANCE: one-way launches still hand over, and an instance that does not reply refuses launches that wait")]
    public async Task OneWayCompatibilityAsync()
    {
        string directory = Directory();
        try
        {
            // A running instance that replies still takes launches that only hand over their arguments.
            await using (var instance = await ClaimPrimaryAsync(directory, Replies))
            {
                var launches = Channel.CreateUnbounded<ApplicationLaunch>();
                instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);
                var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["--show"], "/"), directory, default);
                Check(claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded { Reply: null } }, "a one-way launch is forwarded without a reply");
                var received = await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(Wait);
                Check(received.Arguments.Single() == "--show" && received.Reply is null, "the running instance receives it without a reply");
            }

            // A running instance claimed without replies refuses, before any handler sees the launch.
            await using (var instance = await ClaimPrimaryAsync(directory, new ApplicationInstanceOptions()))
            {
                var launches = Channel.CreateUnbounded<ApplicationLaunch>();
                instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);
                var claim = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["--run"], "/"), Replies, directory, default).AsTask().WaitScaledAsync(Wait);
                Check(claim is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.ResourceBusy, Diagnostic: { Domain: "ApplicationInstance", Code: 0 } },
                    $"an instance that does not reply is busy, not {claim}");
                Check(!launches.Reader.TryRead(out _), "the refused launch is not handled");
            }

            // An earlier SDK closes the connection when it reads the reply marker as a negative launch length.
            string lockPath = System.IO.Directory.GetFiles(directory, "*.lock").Single();
            string socketPath = Path.ChangeExtension(lockPath, ".sock");
            using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
            listener.Listen(16);
            int connections = 0;
            var earlier = Task.Run(async () =>
            {
                while (true)
                {
                    using var client = await listener.AcceptAsync();
                    Interlocked.Increment(ref connections);
                    var length = new byte[4];
                    await client.ReceiveAsync(length);
                    if (BitConverter.ToInt32(length) is > 0) throw new InvalidOperationException("a launch that waits is not a valid earlier launch");
                }
            });
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var old = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(["--run"], "/"), Replies, directory, default).AsTask().WaitScaledAsync(Wait);
            Check(old is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.ResourceBusy, Diagnostic: { Domain: "ApplicationInstance", Code: 0 } },
                $"an earlier instance is busy, not {old}");
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"and reported promptly, not after the claim deadline ({stopwatch.Elapsed})");
            Check(Volatile.Read(ref connections) >= 3 && !earlier.IsFaulted, "the earlier instance only saw negative lengths");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("INSTANCE: IDs, launches and directories others can enter are refused")]
    public async Task ValidationAsync()
    {
        Throws<ArgumentException>(() => ApplicationInstance.ClaimAsync("../escape", new ApplicationLaunch([], "/")).AsTask().GetAwaiter().GetResult());
        Throws<ArgumentException>(() => ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch(default, "/")).AsTask().GetAwaiter().GetResult());
        Throws<ArgumentException>(() => ApplicationInstance.Encode(new ApplicationLaunch([new string('a', 70_000)], "/")));
        Check(ApplicationLaunch.FromProcess(["a"]).WorkingDirectory == Environment.CurrentDirectory, "this process's directory");
        if (OperatingSystem.IsWindows()) return;
        string directory = Directory();
        try
        {
            System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);
            var shared = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default);
            Check(shared is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.IOError }, "a directory others can enter is refused");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    // WaitAsync(Wait), naming the step that timed out (#352): the NativeAOT stack has no line numbers.
    private static async Task<T> Within<T>(Task<T> task, string step)
    {
        try { return await task.WaitScaledAsync(Wait, caller: step); }
        catch (TimeoutException error) { throw new TimeoutException($"Timed out after {TestTimeouts.Scale(Wait).TotalSeconds:0} s: {step}.", error); }
    }

    private static async Task Within(Task task, string step)
    {
        try { await task.WaitScaledAsync(Wait, caller: step); }
        catch (TimeoutException error) { throw new TimeoutException($"Timed out after {TestTimeouts.Scale(Wait).TotalSeconds:0} s: {step}.", error); }
    }
}
