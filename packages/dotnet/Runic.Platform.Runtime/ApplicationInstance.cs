using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Runic.Platform;

namespace Runic.Platform.Runtime;

/// <summary>
/// One running instance per application and user: a later launch hands its arguments to the running instance and
/// exits. Works the same on Linux, Windows and macOS. On Windows the launching process also lets the running instance
/// take the foreground (<c>AllowSetForegroundWindow</c>), so it can bring its window forward.
/// </summary>
/// <remarks>
/// The running instance holds an exclusive lock file, which the operating system releases when the process ends,
/// and listens on a Unix domain socket in a directory only the user can use: <c>XDG_RUNTIME_DIR</c> on Linux (the
/// application's directory inside a Flatpak sandbox), the per-user temporary directory on macOS, and local
/// application data on Windows.
/// </remarks>
public static class ApplicationInstance
{
    private const int MaxArguments = 1024, MaxLength = 64 * 1024, MaxSocketPath = 100;
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);
    // The running instance answers within ForwardTimeout of accepting; the launching process waits a little longer.
    private static readonly TimeSpan ReplyTimeout = ForwardTimeout + TimeSpan.FromSeconds(2);

    /// <summary>
    /// Becomes the running instance of <paramref name="applicationId"/>, or forwards <paramref name="launch"/> to it.
    /// </summary>
    /// <param name="applicationId">The application's reverse-DNS ID, such as <c>org.example.App</c>.</param>
    /// <param name="launch">This process's launch, usually <see cref="ApplicationLaunch.FromProcess"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the running instance.</param>
    /// <returns>
    /// <see cref="ApplicationInstanceClaim.Primary"/> or <see cref="ApplicationInstanceClaim.Forwarded"/>; unavailable
    /// without a per-user runtime directory or when its path is too long for a socket, and
    /// <see cref="PlatformFailureCode.ResourceBusy"/> when another instance
    /// holds the lock but does not accept the launch. The application decides whether to run anyway.
    /// </returns>
    public static ValueTask<PlatformResult<ApplicationInstanceClaim>> ClaimAsync(string applicationId, ApplicationLaunch launch,
        CancellationToken cancellationToken = default) => ClaimAsync(applicationId, launch, null, cancellationToken);

    internal static async ValueTask<PlatformResult<ApplicationInstanceClaim>> ClaimAsync(string applicationId, ApplicationLaunch launch,
        string? directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 255 || applicationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Use the application's reverse-DNS ID.", nameof(applicationId));
        ArgumentNullException.ThrowIfNull(launch);
        Validate(launch);
        directory ??= RuntimeDirectory();
        if (directory is null) return new PlatformResult<ApplicationInstanceClaim>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        // Short names: Unix socket paths are limited to about 100 bytes.
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)))[..16];
        string socketPath = Path.Combine(directory, name + ".sock"), lockPath = Path.Combine(directory, name + ".lock");
        if (Encoding.UTF8.GetByteCount(socketPath) > MaxSocketPath)
            return new PlatformResult<ApplicationInstanceClaim>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        try
        {
            CreatePrivateDirectory(directory);
            // Until the deadline: become the running instance once the lock is free, or hand the launch to the
            // instance that holds it. That instance may still be starting, or ending.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ForwardTimeout);
            try
            {
                while (true)
                {
                    if (TryListen(lockPath, socketPath) is { } instance)
                        return new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Primary(instance));
                    if (await TryForwardAsync(socketPath, launch, deadline.Token, cancellationToken).ConfigureAwait(false))
                        return new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Forwarded());
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PlatformResult<ApplicationInstanceClaim>.Failed(PlatformFailureCode.ResourceBusy);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException)
        {
            return new PlatformResult<ApplicationInstanceClaim>.Failed(PlatformFailureCode.IoError);
        }
    }

    // Null while another process holds the lock.
    private static RunningInstance? TryListen(string lockPath, string socketPath)
    {
        FileStream lockFile;
        try { lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (File.Exists(lockPath)) { return null; }
        try
        {
            File.Delete(socketPath); // Left by an instance that ended without cleaning up.
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(16);
            }
            catch { listener.Dispose(); throw; }
            return new RunningInstance(listener, lockFile, socketPath);
        }
        catch { lockFile.Dispose(); throw; }
    }

    // False when no instance listens yet, or it did not take the launch. Once sent, the reply is awaited past the
    // claim deadline, so a launch the instance took is never also reported busy.
    private static async Task<bool> TryForwardAsync(string socketPath, ApplicationLaunch launch, CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline).ConfigureAwait(false);
            // Before the launch arrives, so the instance may raise its window when it handles it.
            ForegroundRights.GrantPeer(socket);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(Encode(launch), deadline).ConfigureAwait(false);
            using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            replyTimeout.CancelAfter(ReplyTimeout);
            var reply = new byte[1];
            return await stream.ReadAsync(reply, replyTimeout.Token).ConfigureAwait(false) == 1 && reply[0] == 1;
        }
        catch (Exception error) when (error is SocketException or IOException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !deadline.IsCancellationRequested) { return false; }
    }

    private static string? RuntimeDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runic", "instances");
        if (OperatingSystem.IsMacOS()) return Path.Combine(Path.GetTempPath(), "runic-instances");
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathFullyQualified(runtime)) return null;
        // Instances of one Flatpak application share only its own directory below the runtime directory.
        string? flatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        return Path.Combine(runtime, string.IsNullOrEmpty(flatpak) ? "runic-instances" : Path.Combine("app", flatpak, "runic-instances"));
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); return; }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Refuse a directory someone else can enter: its socket would accept their launches.
        if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("The instance directory is accessible to other users.");
    }

    private static void Validate(ApplicationLaunch launch)
    {
        if (launch.Arguments.IsDefault || launch.Arguments.Length > MaxArguments)
            throw new ArgumentException("A launch has at most 1024 arguments.", nameof(launch));
        ArgumentNullException.ThrowIfNull(launch.WorkingDirectory);
        if (launch.Arguments.Any(argument => argument is null))
            throw new ArgumentException("Launch arguments cannot be null.", nameof(launch));
    }

    // Wire format: the total length, then strings as UTF-8 with their lengths.
    internal static byte[] Encode(ApplicationLaunch launch)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(launch.Arguments.Length);
            foreach (var argument in launch.Arguments) writer.Write(argument);
            writer.Write(launch.WorkingDirectory);
            writer.Write(launch.PlatformContext?.ActivationToken ?? "");
            writer.Write(launch.PlatformContext?.StartupId ?? "");
        }
        if (body.Length > MaxLength) throw new ArgumentException("The launch is too large to forward.", nameof(launch));
        var message = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(message, (int)body.Length);
        body.ToArray().CopyTo(message, 4);
        return message;
    }

    internal static ApplicationLaunch Decode(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
        int count = reader.ReadInt32();
        if (count is < 0 or > MaxArguments) throw new InvalidDataException("Invalid launch.");
        var arguments = new string[count];
        for (int i = 0; i < count; i++) arguments[i] = reader.ReadString();
        string directory = reader.ReadString(), token = reader.ReadString(), startupId = reader.ReadString();
        DesktopActivationContext? context = null;
        if (token.Length != 0 || startupId.Length != 0)
        {
            try { context = new DesktopActivationContext(token.Length == 0 ? null : token, startupId.Length == 0 ? null : startupId); }
            catch (ArgumentException) { /* Unusable activation data cannot raise a window. */ }
        }
        return new ApplicationLaunch([.. arguments], directory) { PlatformContext = context };
    }

    private sealed class RunningInstance : IApplicationInstance
    {
        private readonly Socket _listener;
        private readonly FileStream _lock;
        private readonly string _socketPath;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<Pending> _launches = Channel.CreateUnbounded<Pending>(new() { SingleReader = true });
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private EventHandler<ApplicationLaunch>? _launched;
        private readonly Task _accept;
        private readonly CancellationToken _stopping;
        private int _disposed;

        internal RunningInstance(Socket listener, FileStream lockFile, string socketPath)
        {
            _listener = listener; _lock = lockFile; _socketPath = socketPath;
            // Read once: the source is disposed while the pump may still be starting.
            _stopping = _stop.Token;
            _accept = Task.Run(AcceptAsync);
            // Not awaited on disposal: a Launched handler may be the one disposing.
            _ = Task.Run(PumpAsync);
        }

        // Launches wait for the first handler, so one that arrives during start-up is not lost.
        public event EventHandler<ApplicationLaunch>? Launched
        {
            add { lock (_gate) _launched += value; if (value is not null) _subscribed.TrySetResult(); }
            remove { lock (_gate) _launched -= value; }
        }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                Socket client;
                try { client = await _listener.AcceptAsync(_stopping).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = ReceiveAsync(client);
            }
        }

        // The launching process hears 1 once its launch is handed to a handler. Anything else, or a closed connection,
        // tells it to try again: this instance may be ending.
        private async Task ReceiveAsync(Socket client)
        {
            using var connection = client;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(ForwardTimeout);
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                var length = new byte[4];
                await stream.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                int size = BitConverter.ToInt32(length);
                if (size is <= 0 or > MaxLength) return;
                var body = new byte[size];
                await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var pending = new Pending(Decode(body));
                bool delivered = false;
                if (_launches.Writer.TryWrite(pending))
                {
                    try { delivered = await pending.Delivered.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
                    // No handler took it in time; a handler that already started it still counts.
                    catch (OperationCanceledException) when (!pending.Abandon()) { delivered = await pending.Delivered.Task.ConfigureAwait(false); }
                }
                await stream.WriteAsync(new[] { delivered ? (byte)1 : (byte)0 }).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or InvalidDataException
                or EndOfStreamException or FormatException or ObjectDisposedException)
            {
                /* A broken or malformed launch is dropped; its process reports that it could not forward. */
            }
        }

        private async Task PumpAsync()
        {
            try { await _subscribed.Task.WaitAsync(_stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await foreach (var pending in _launches.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                EventHandler<ApplicationLaunch>? handlers;
                lock (_gate) handlers = _launched;
                // Without a handler, or while ending, the launching process tries again and may become the next instance.
                if (handlers is null || Volatile.Read(ref _disposed) != 0 || !pending.Start()) { pending.Delivered.TrySetResult(false); continue; }
                // Answered before the handler runs, so a slow handler is not reported as busy.
                pending.Delivered.TrySetResult(true);
                try { handlers(this, pending.Launch); }
                catch (Exception error)
                {
                    // As with an event raised on the thread pool, a throwing handler fails the process.
                    var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                    ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Dispose();
            _launches.Writer.TryComplete();
            // Launches still queued were not handled; their processes try again and may become the next instance.
            while (_launches.Reader.TryRead(out var pending)) pending.Delivered.TrySetResult(false);
            try { await _accept.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { File.Delete(_socketPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // Released last, so a new instance never finds a socket file it did not create.
            await _lock.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }

        private sealed class Pending(ApplicationLaunch launch)
        {
            private int _state; // 0 queued, 1 handed to the handlers, 2 abandoned by its connection
            public ApplicationLaunch Launch => launch;
            public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Start() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
            public bool Abandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        }
    }
}
