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
            var received = await launches.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Check(received.Arguments.SequenceEqual(launch.Arguments) && received.WorkingDirectory == "/home/alice"
                && received.PlatformContext?.ActivationToken == "token-1" && received.PlatformContext.StartupId is null, "arguments, directory and token arrive");

            var other = await ApplicationInstance.ClaimAsync("org.example.Other", new ApplicationLaunch([], "/"), directory, default);
            Check(other is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "another application has its own instance");
            await ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)other).Value).Instance.DisposeAsync();

            for (int i = 0; i < 3; i++)
                Check(await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([i.ToString(System.Globalization.CultureInfo.InvariantCulture)], "/"), directory, default)
                    is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "later launches are forwarded too");
            for (int i = 0; i < 3; i++)
                Check((await launches.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Arguments.Single() == i.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            Check(await second.WaitAsync(Wait) is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "the waiting launch is forwarded");
            Check((await launches.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Arguments.Single() == "early", "the first handler receives it");
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
            var second = await ApplicationInstance.ClaimAsync("org.example.App", new ApplicationLaunch([], "/"), directory, default).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
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
            var next = await second.WaitAsync(Wait);
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

    // The launching process grants the running instance the foreground through the socket's peer process ID.
    [Test, DisplayName("INSTANCE: on Windows a forwarding process finds the running instance's process")]
    public async Task FindsTheRunningInstanceProcessAsync()
    {
        string directory = Directory();
        System.IO.Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "peer.sock");
        try
        {
            using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            using var accepted = await listener.AcceptAsync();
            int? peer = ForegroundRights.PeerProcessId(client);
            if (OperatingSystem.IsWindows())
            {
                Check(peer == Environment.ProcessId, $"the peer is this process, not {peer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
                // Granting needs no window and must not throw.
                ForegroundRights.GrantPeer(client);
            }
            else Check(peer is null, "other systems do not ask");
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
            Check(shared is PlatformResult<ApplicationInstanceClaim>.Failed { Code: PlatformFailureCode.IoError }, "a directory others can enter is refused");
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
}
