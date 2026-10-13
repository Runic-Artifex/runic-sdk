using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Runic.Platform;

namespace Runic.Platform.Runtime;

/// <summary>
/// One running instance per application and user: a later launch hands its arguments to the running instance and
/// exits. Works the same on Linux, Windows and macOS, without native interop.
/// </summary>
/// <remarks>
/// The running instance holds an exclusive lock file, which the operating system releases when the process ends,
/// and listens on a Unix domain socket in a directory only the user can use: <c>XDG_RUNTIME_DIR</c> on Linux (the
/// application's directory inside a Flatpak sandbox), the per-user temporary directory on macOS, and local
/// application data on Windows.
/// </remarks>
public static class ApplicationInstance
{
    private const int MaxArguments = 1024, MaxLength = 64 * 1024;
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Becomes the running instance of <paramref name="applicationId"/>, or forwards <paramref name="launch"/> to it.
    /// </summary>
    /// <param name="applicationId">The application's reverse-DNS ID, such as <c>org.example.App</c>.</param>
    /// <param name="launch">This process's launch, usually <see cref="ApplicationLaunch.FromProcess"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the running instance.</param>
    /// <returns>
    /// <see cref="ApplicationInstanceClaim.Primary"/> or <see cref="ApplicationInstanceClaim.Forwarded"/>; unavailable
    /// without a per-user runtime directory, and <see cref="PlatformFailureCode.ResourceBusy"/> when another instance
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
        try
        {
            CreatePrivateDirectory(directory);
            // Short names: Unix socket paths are limited to about 100 bytes.
            string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)))[..16];
            string socketPath = Path.Combine(directory, name + ".sock"), lockPath = Path.Combine(directory, name + ".lock");
            FileStream? lockFile = null;
            try { lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { /* Another process is the running instance. */ }
            if (lockFile is not null)
            {
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
                    return new PlatformResult<ApplicationInstanceClaim>.Success(
                        new ApplicationInstanceClaim.Primary(new RunningInstance(listener, lockFile, socketPath)));
                }
                catch { await lockFile.DisposeAsync().ConfigureAwait(false); throw; }
            }
            return await ForwardAsync(socketPath, launch, cancellationToken).ConfigureAwait(false)
                ? new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Forwarded())
                : new PlatformResult<ApplicationInstanceClaim>.Failed(PlatformFailureCode.ResourceBusy);
        }
        catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException)
        {
            return new PlatformResult<ApplicationInstanceClaim>.Failed(PlatformFailureCode.IoError);
        }
    }

    // The running instance may still be starting: it holds the lock before it listens.
    private static async Task<bool> ForwardAsync(string socketPath, ApplicationLaunch launch, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ForwardTimeout);
        try
        {
            while (true)
            {
                using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                {
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token).ConfigureAwait(false);
                        await using var stream = new NetworkStream(socket, ownsSocket: false);
                        await stream.WriteAsync(Encode(launch), deadline.Token).ConfigureAwait(false);
                        var reply = new byte[1];
                        return await stream.ReadAsync(reply, deadline.Token).ConfigureAwait(false) == 1 && reply[0] == 1;
                    }
                    catch (Exception error) when (error is SocketException or IOException) { /* Not listening yet; try again. */ }
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
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
        private readonly Channel<ApplicationLaunch> _launches = Channel.CreateUnbounded<ApplicationLaunch>(new() { SingleReader = true });
        private readonly Task _accept;
        private int _disposed;

        internal RunningInstance(Socket listener, FileStream lockFile, string socketPath)
        {
            _listener = listener; _lock = lockFile; _socketPath = socketPath;
            _accept = Task.Run(AcceptAsync);
            // Not awaited on disposal: a Launched handler may be the one disposing.
            _ = Task.Run(PumpAsync);
        }

        public event EventHandler<ApplicationLaunch>? Launched;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try { client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = ReceiveAsync(client);
            }
        }

        private async Task ReceiveAsync(Socket client)
        {
            using var connection = client;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
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
                var launch = Decode(body);
                _launches.Writer.TryWrite(launch);
                await stream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or InvalidDataException or EndOfStreamException)
            {
                /* A broken or malformed launch is dropped; its process reports that it could not forward. */
            }
        }

        private async Task PumpAsync()
        {
            await foreach (var launch in _launches.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try { Launched?.Invoke(this, launch); }
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
            try { await _accept.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { File.Delete(_socketPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // Released last, so a new instance never finds a socket file it did not create.
            await _lock.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
