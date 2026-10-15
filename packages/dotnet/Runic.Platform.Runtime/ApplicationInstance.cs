using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// <para>
/// With <see cref="ApplicationInstanceOptions.ReplyToLaunches"/> a later launch also waits for the running instance's
/// reply, a stream of messages and an exit status, over the same socket: a command line can hand its work to the
/// running instance and report the outcome. <see cref="ApplicationLaunch.Reply"/> is the running instance's side,
/// <see cref="ApplicationInstanceClaim.Forwarded.Reply"/> the launching process's.
/// </para>
/// </remarks>
public static class ApplicationInstance
{
    private const int MaxArguments = 1024, MaxLength = 64 * 1024, MaxSocketPath = 100, MaxMessage = 1024 * 1024;
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);
    // The running instance answers within ForwardTimeout of accepting; the launching process waits a little longer.
    private static readonly TimeSpan ReplyTimeout = ForwardTimeout + TimeSpan.FromSeconds(2);
    private static readonly ApplicationInstanceOptions HandOver = new();

    // A launch that waits for a reply starts with this negative length, then the protocol version, both 32-bit.
    // Instances without replies (SDK 0.8.0-preview.2 and earlier) read a negative length as a malformed launch and
    // close the connection without handling it, so the launch is never half handled.
    private const int ReplyMarker = -0x5255_4E43, ReplyProtocol = 1;
    // Version 1 continues in frames: a type byte, the payload's 32-bit little-endian length, then the payload.
    //   launching process: hello (marker, version)          running instance: Accepted(version) or Refused(version)
    //   launching process: Launch(the launch, as Encode)    running instance: Delivered or NotDelivered
    //   running instance: Message(UTF-8)* then Exit(int32)  launching process, at any time: Cancel
    // Either side closing the connection ends the reply; before Exit, that cancels the launch or reports it ended.
    private const int FrameHeader = 5;
    private const byte Accepted = 1, Refused = 2, Delivered = 3, NotDelivered = 4, Message = 5, Exit = 6, Launch = 16, Cancel = 17;
    // A launching process that sees this many connections close without an answer in a row talks to an instance
    // without replies. Ending instances close their listener first, so they are not counted for long.
    private const int UnansweredAttempts = 3;

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
        CancellationToken cancellationToken = default) => ClaimAsync(applicationId, launch, HandOver, null, cancellationToken);

    /// <summary>
    /// Becomes the running instance of <paramref name="applicationId"/>, or forwards <paramref name="launch"/> to it,
    /// as <paramref name="options"/> describe.
    /// </summary>
    /// <param name="applicationId">The application's reverse-DNS ID, such as <c>org.example.App</c>.</param>
    /// <param name="launch">This process's launch, usually <see cref="ApplicationLaunch.FromProcess"/>.</param>
    /// <param name="options">Whether the launches of this application are answered.</param>
    /// <param name="cancellationToken">Cancels waiting for the running instance.</param>
    /// <returns>
    /// As <see cref="ClaimAsync(string, ApplicationLaunch, CancellationToken)"/>. With
    /// <see cref="ApplicationInstanceOptions.ReplyToLaunches"/>, <see cref="ApplicationInstanceClaim.Forwarded.Reply"/>
    /// is set, and a running instance that does not reply is <see cref="PlatformFailureCode.ResourceBusy"/> with an
    /// <c>ApplicationInstance</c> diagnostic whose code is the reply protocol version that instance supports, 0 for
    /// none; the launch was not handled.
    /// </returns>
    public static ValueTask<PlatformResult<ApplicationInstanceClaim>> ClaimAsync(string applicationId, ApplicationLaunch launch,
        ApplicationInstanceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ClaimAsync(applicationId, launch, options, null, cancellationToken);
    }

    internal static ValueTask<PlatformResult<ApplicationInstanceClaim>> ClaimAsync(string applicationId, ApplicationLaunch launch,
        string? directory, CancellationToken cancellationToken) => ClaimAsync(applicationId, launch, HandOver, directory, cancellationToken);

    internal static async ValueTask<PlatformResult<ApplicationInstanceClaim>> ClaimAsync(string applicationId, ApplicationLaunch launch,
        ApplicationInstanceOptions options, string? directory, CancellationToken cancellationToken)
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
                int unanswered = 0;
                while (true)
                {
                    if (TryListen(lockPath, socketPath, options.ReplyToLaunches) is { } instance)
                        return new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Primary(instance));
                    if (!options.ReplyToLaunches)
                    {
                        if (await TryForwardAsync(socketPath, launch, deadline.Token, cancellationToken).ConfigureAwait(false))
                            return new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Forwarded());
                    }
                    else
                    {
                        var attempt = await TryForwardWithReplyAsync(socketPath, launch, deadline.Token, cancellationToken).ConfigureAwait(false);
                        if (attempt.Reply is { } reply)
                            return new PlatformResult<ApplicationInstanceClaim>.Success(new ApplicationInstanceClaim.Forwarded { Reply = reply });
                        if (attempt.RefusedVersion is { } version) return NoReplies(version);
                        unanswered = attempt.Unanswered ? unanswered + 1 : 0;
                        if (unanswered >= UnansweredAttempts) return NoReplies(0);
                    }
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
    private static RunningInstance? TryListen(string lockPath, string socketPath, bool replies)
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
            return new RunningInstance(listener, lockFile, socketPath, replies);
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

    private static PlatformResult<ApplicationInstanceClaim> NoReplies(int version) =>
        new PlatformResult<ApplicationInstanceClaim>.Failed(PlatformFailureCode.ResourceBusy, new PlatformDiagnostic("ApplicationInstance", version,
            version == 0 ? "The running instance does not reply to launches." : "The running instance speaks another reply protocol version."));

    private readonly record struct ForwardAttempt(ForwardedLaunchReply? Reply = null, int? RefusedVersion = null, bool Unanswered = false);

    // Like TryForwardAsync, but the connection stays open for the running instance's reply. Unanswered when the
    // connection closed before the running instance said anything: an instance without replies does that.
    private static async Task<ForwardAttempt> TryForwardWithReplyAsync(string socketPath, ApplicationLaunch launch, CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        bool answered = false, handedOver = false;
        try
        {
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline).ConfigureAwait(false); }
            catch (SocketException) { return default; }
            ForegroundRights.GrantPeer(socket);
            stream = new NetworkStream(socket, ownsSocket: false);
            using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            replyTimeout.CancelAfter(ReplyTimeout);
            var hello = new byte[8];
            BitConverter.TryWriteBytes(hello, ReplyMarker);
            BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(4), ReplyProtocol);
            await stream.WriteAsync(hello, deadline).ConfigureAwait(false);
            var header = new byte[FrameHeader];
            var answer = await ReadFrameAsync(stream, header, replyTimeout.Token).ConfigureAwait(false);
            if (answer is null) return new ForwardAttempt(Unanswered: true);
            answered = true;
            if (answer is not ({ Type: Accepted or Refused, Length: 4 })) return default;
            int version = await ReadInt32Async(stream, replyTimeout.Token).ConfigureAwait(false);
            if (answer.Value.Type == Refused || version != ReplyProtocol) return new ForwardAttempt(RefusedVersion: version);
            await stream.WriteAsync(Frame(Launch, EncodeBody(launch)), deadline).ConfigureAwait(false);
            if (await ReadFrameAsync(stream, header, replyTimeout.Token).ConfigureAwait(false) is not { Type: Delivered, Length: 0 }) return default;
            handedOver = true;
            return new ForwardAttempt(new ForwardedLaunchReply(socket, stream));
        }
        // Before the running instance answered, a closed connection is an instance that does not reply.
        catch (Exception error) when (error is SocketException or IOException) { return new ForwardAttempt(Unanswered: !answered); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !deadline.IsCancellationRequested) { return default; }
        finally
        {
            if (!handedOver)
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
            }
        }
    }

    // Null at the end of the stream between frames.
    private static async ValueTask<(byte Type, int Length)?> ReadFrameAsync(Stream stream, byte[] header, CancellationToken cancellationToken)
    {
        int read = await stream.ReadAtLeastAsync(header, FrameHeader, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < FrameHeader) throw new EndOfStreamException();
        return (header[0], BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1)));
    }

    private static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[4];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[FrameHeader + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(FrameHeader));
        return frame;
    }

    private static byte[] Int32Frame(byte type, int value)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return Frame(type, payload);
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
        var body = EncodeBody(launch);
        var message = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(message, body.Length);
        body.CopyTo(message, 4);
        return message;
    }

    private static byte[] EncodeBody(ApplicationLaunch launch)
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
        return body.ToArray();
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

    // The launching process's side of one reply.
    private sealed class ForwardedLaunchReply(Socket socket, NetworkStream stream) : IForwardedLaunchReply
    {
        private readonly byte[] _header = new byte[FrameHeader];
        private byte[] _payload = new byte[4096];
        private PlatformResult<int>? _result;
        private int _reading, _cancelRequested, _disposed;

        public async IAsyncEnumerable<string> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Read the reply from one caller at a time.");
            try
            {
                while (await ReadMessageAsync(cancellationToken).ConfigureAwait(false) is { } message) yield return message;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        public async ValueTask<PlatformResult<int>> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await foreach (var _ in ReadMessagesAsync(cancellationToken).ConfigureAwait(false)) { }
            return Volatile.Read(ref _result)!;
        }

        // Null once the reply ended, with its result.
        private async ValueTask<string?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _result) is not null) return null;
            try
            {
                switch (await ReadFrameAsync(stream, _header, cancellationToken).ConfigureAwait(false))
                {
                    case null: return End(new PlatformResult<int>.Failed(PlatformFailureCode.IoError));
                    case { Type: Message, Length: >= 0 and <= MaxMessage } frame:
                        if (_payload.Length < frame.Length) _payload = new byte[Math.Max(frame.Length, Math.Min(_payload.Length * 2, MaxMessage))];
                        await stream.ReadExactlyAsync(_payload.AsMemory(0, frame.Length), cancellationToken).ConfigureAwait(false);
                        return Encoding.UTF8.GetString(_payload, 0, frame.Length);
                    case { Type: Exit, Length: 4 }:
                        return End(new PlatformResult<int>.Success(await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false)));
                    default: return End(new PlatformResult<int>.Failed(PlatformFailureCode.InvalidData));
                }
            }
            // Part of a frame may have been read: the reply cannot continue, and closing it cancels the launch.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                End(new PlatformResult<int>.Failed(PlatformFailureCode.IoError));
                throw;
            }
            catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
            {
                return End(new PlatformResult<int>.Failed(PlatformFailureCode.IoError));
            }
        }

        private string? End(PlatformResult<int> result)
        {
            Interlocked.CompareExchange(ref _result, result, null);
            Close();
            return null;
        }

        public async ValueTask CancelAsync()
        {
            if (Volatile.Read(ref _result) is not null || Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
            try { await stream.WriteAsync(Frame(Cancel, [])).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { /* The reply already ended. */ }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.CompareExchange(ref _result, new PlatformResult<int>.Failed(PlatformFailureCode.IoError), null);
                Close();
            }
            return ValueTask.CompletedTask;
        }

        private void Close()
        {
            stream.Dispose();
            socket.Dispose();
        }
    }

    private sealed class RunningInstance : IApplicationInstance
    {
        private readonly Socket _listener;
        private readonly FileStream _lock;
        private readonly string _socketPath;
        private readonly bool _replies;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<Pending> _launches = Channel.CreateUnbounded<Pending>(new() { SingleReader = true });
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly HashSet<LaunchReply> _open = [];
        private EventHandler<ApplicationLaunch>? _launched;
        private readonly Task _accept;
        private readonly CancellationToken _stopping;
        private int _disposed;

        internal RunningInstance(Socket listener, FileStream lockFile, string socketPath, bool replies)
        {
            _listener = listener; _lock = lockFile; _socketPath = socketPath; _replies = replies;
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
            var stream = new NetworkStream(client, ownsSocket: false);
            bool handedOver = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(ForwardTimeout);
            try
            {
                var length = new byte[4];
                await stream.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                int size = BitConverter.ToInt32(length);
                if (size == ReplyMarker)
                {
                    handedOver = await ReceiveWithReplyAsync(client, stream, timeout.Token).ConfigureAwait(false);
                    return;
                }
                if (size is <= 0 or > MaxLength) return;
                var body = new byte[size];
                await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                bool delivered = await DeliverAsync(new Pending(Decode(body)), timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(new[] { delivered ? (byte)1 : (byte)0 }).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or InvalidDataException
                or EndOfStreamException or FormatException or ObjectDisposedException)
            {
                /* A broken or malformed launch is dropped; its process reports that it could not forward. */
            }
            finally
            {
                if (!handedOver)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    client.Dispose();
                }
            }
        }

        // True once the connection belongs to the launch's reply.
        private async Task<bool> ReceiveWithReplyAsync(Socket client, NetworkStream stream, CancellationToken timeout)
        {
            int version = await ReadInt32Async(stream, timeout).ConfigureAwait(false);
            if (!_replies || version != ReplyProtocol)
            {
                await stream.WriteAsync(Int32Frame(Refused, _replies ? ReplyProtocol : 0), timeout).ConfigureAwait(false);
                return false;
            }
            await stream.WriteAsync(Int32Frame(Accepted, ReplyProtocol), timeout).ConfigureAwait(false);
            if (await ReadFrameAsync(stream, new byte[FrameHeader], timeout).ConfigureAwait(false) is not { Type: Launch, Length: > 0 and <= MaxLength } frame)
                return false;
            var body = new byte[frame.Length];
            await stream.ReadExactlyAsync(body, timeout).ConfigureAwait(false);
            var reply = new LaunchReply(client, stream, this);
            if (!await DeliverAsync(new Pending(Decode(body) with { Reply = reply }), timeout).ConfigureAwait(false))
            {
                // The handlers never saw the reply; the launching process tries again.
                await stream.WriteAsync(Frame(NotDelivered, []), timeout).ConfigureAwait(false);
                return false;
            }
            // A handler may already write: the reply sends its messages after Delivered.
            await reply.StartAsync().ConfigureAwait(false);
            return true;
        }

        private async Task<bool> DeliverAsync(Pending pending, CancellationToken timeout)
        {
            if (!_launches.Writer.TryWrite(pending)) return false;
            try { return await pending.Delivered.Task.WaitAsync(timeout).ConfigureAwait(false); }
            // No handler took it in time; a handler that already started it still counts.
            catch (OperationCanceledException) when (!pending.Abandon()) { return await pending.Delivered.Task.ConfigureAwait(false); }
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
                catch (Exception error) { Rethrow(error); }
            }
        }

        // As with an event raised on the thread pool, a throwing handler fails the process.
        private static void Rethrow(Exception error) =>
            ThreadPool.QueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), ExceptionDispatchInfo.Capture(error));

        // False once this instance is ending: the reply is closed instead.
        private bool Track(LaunchReply reply)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                _open.Add(reply);
                return true;
            }
        }

        private void Untrack(LaunchReply reply) { lock (_gate) _open.Remove(reply); }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Dispose();
            _launches.Writer.TryComplete();
            // Launches still queued were not handled; their processes try again and may become the next instance.
            while (_launches.Reader.TryRead(out var pending)) pending.Delivered.TrySetResult(false);
            // Replies still open end without an exit status; their launching processes report that this instance ended.
            LaunchReply[] open;
            lock (_gate) open = [.. _open];
            foreach (var reply in open) reply.Close(cancel: true);
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

        // The running instance's side of one reply. Frames go out one at a time; the connection's other direction
        // carries only Cancel, read until the launching process closes it.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
            Justification = "Neither the semaphore nor the token source has a wait handle or timer; a handler may use the token after the reply ends.")]
        private sealed class LaunchReply(Socket socket, NetworkStream stream, RunningInstance owner) : IApplicationLaunchReply
        {
            // Held until Delivered is sent, so a handler's first message follows it.
            private readonly SemaphoreSlim _writing = new(0, 1);
            private readonly CancellationTokenSource _cancelled = new();
            private int _completed, _closed;
            private bool _exitSent; // Under _writing.

            public CancellationToken Cancelled => _cancelled.Token;

            internal async Task StartAsync()
            {
                try
                {
                    await stream.WriteAsync(Frame(Delivered, [])).ConfigureAwait(false);
                    if (!owner.Track(this)) { Close(cancel: true); return; }
                    _ = Task.Run(ReadAsync);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { Close(cancel: true); }
                finally { _writing.Release(); }
            }

            public async ValueTask<bool> WriteAsync(string message, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(message);
                int size = Encoding.UTF8.GetByteCount(message);
                if (size > MaxMessage) throw new ArgumentException("A message is at most 1 MiB in UTF-8.", nameof(message));
                if (Volatile.Read(ref _completed) != 0) throw new InvalidOperationException("The reply was completed.");
                var frame = ArrayPool<byte>.Shared.Rent(FrameHeader + size);
                frame[0] = Message;
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), size);
                Encoding.UTF8.GetBytes(message, frame.AsSpan(FrameHeader));
                bool sent = await SendAsync(frame.AsMemory(0, FrameHeader + size), exit: false, cancellationToken).ConfigureAwait(false);
                // A write that was cancelled or broken may still hold the buffer; it is left to the collector.
                if (sent) ArrayPool<byte>.Shared.Return(frame);
                return sent;
            }

            public async ValueTask CompleteAsync(int exitCode, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) throw new InvalidOperationException("The reply was already completed.");
                try { await SendAsync(Int32Frame(Exit, exitCode), exit: true, cancellationToken).ConfigureAwait(false); }
                finally { Close(cancel: false); }
            }

            private async ValueTask<bool> SendAsync(ReadOnlyMemory<byte> frame, bool exit, CancellationToken cancellationToken)
            {
                await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_exitSent) throw new InvalidOperationException("The reply was completed.");
                    _exitSent = exit;
                    if (Volatile.Read(ref _closed) != 0) return false;
                    await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (exit) socket.Shutdown(SocketShutdown.Send);
                    return true;
                }
                // Part of the frame may have gone out, so the reply cannot continue.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Close(cancel: true); throw; }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { Close(cancel: true); return false; }
                finally { _writing.Release(); }
            }

            // Cancel requests, until the launching process closes its side or breaks the protocol.
            private async Task ReadAsync()
            {
                var header = new byte[FrameHeader];
                try
                {
                    while (await ReadFrameAsync(stream, header, CancellationToken.None).ConfigureAwait(false) is { Type: Cancel, Length: 0 })
                        Signal();
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or EndOfStreamException) { }
                Close(cancel: true);
            }

            internal void Close(bool cancel)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                owner.Untrack(this);
                stream.Dispose();
                socket.Dispose();
                // Not after an exit status: the launch was handled.
                if (cancel && !Volatile.Read(ref _exitSent)) Signal();
            }

            private void Signal()
            {
                try { _cancelled.Cancel(); }
                catch (Exception error) { Rethrow(error); }
            }
        }
    }
}

/// <summary>How <see cref="ApplicationInstance.ClaimAsync(string, ApplicationLaunch, ApplicationInstanceOptions, CancellationToken)"/> forwards launches.</summary>
public sealed record ApplicationInstanceOptions
{
    /// <summary>
    /// Whether launches of this application are answered. A later launch waits for the running instance's messages
    /// and exit status in <see cref="ApplicationInstanceClaim.Forwarded.Reply"/>; as the running instance, launches
    /// from such processes carry <see cref="ApplicationLaunch.Reply"/>. Launches that only hand over their arguments
    /// still arrive, without a reply.
    /// </summary>
    /// <remarks>
    /// Set it in every process of the application, so the running instance answers whichever launch comes later. An
    /// instance claimed without it, or by an SDK before 0.8.0-preview.3, refuses launches that wait for a reply rather
    /// than leaving them unanswered.
    /// </remarks>
    public bool ReplyToLaunches { get; init; }
}
