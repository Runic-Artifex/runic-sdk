namespace Runic.Platform.Windows.Elevation;

/// <summary>The helper's end of the pipe: the greeting, the start signal, cancellation and output.</summary>
internal sealed class HelperChannel(Stream pipe) : IDisposable
{
    private readonly Lock _writeLock = new();
    private readonly CancellationTokenSource _cancelled = new();
    private volatile bool _broken;

    /// <summary>Cancelled when the application sends a cancellation, closes the pipe or ends.</summary>
    public CancellationToken Cancelled => _cancelled.Token;

    /// <exception cref="IOException">The application closed the pipe.</exception>
    public void SendHello(HelperStatus status)
    {
        lock (_writeLock) ElevationProtocol.WriteHello(pipe, status);
    }

    /// <summary>
    /// Waits for the application's start signal. False when it closes the pipe, cancels or sends anything else
    /// first, or after <paramref name="timeout"/>: the action must not start then.
    /// </summary>
    public async Task<bool> WaitForGoAsync(TimeSpan timeout)
    {
        using var expired = new CancellationTokenSource(timeout);
        var buffer = new byte[ElevationProtocol.MaximumPayload];
        try
        {
            return await ElevationProtocol.ReadFrameAsync(pipe, buffer, expired.Token).ConfigureAwait(false) is (ElevationProtocol.Go, _);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Reads the application's messages in the background until a cancellation or the end of the pipe.</summary>
    public void StartWatching()
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[ElevationProtocol.MaximumPayload];
            try
            {
                while (ElevationProtocol.ReadFrameAsync(pipe, buffer, CancellationToken.None).AsTask().GetAwaiter().GetResult() is var (type, _))
                    if (type == ElevationProtocol.Cancel) break;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException) { }
            _cancelled.Cancel();
        })
        {
            IsBackground = true,
            Name = "Runic elevation cancellation",
        };
        thread.Start();
    }

    /// <summary>
    /// Sends output of <paramref name="channel"/> (<see cref="ElevationProtocol.StandardOutput"/> or
    /// <see cref="ElevationProtocol.StandardError"/>). Blocks while the application does not read, which bounds
    /// memory. False once the application is gone; later output is dropped.
    /// </summary>
    public bool Write(byte channel, ReadOnlySpan<byte> data)
    {
        if (_broken) return false;
        try
        {
            lock (_writeLock)
            {
                while (data.Length > 0)
                {
                    var part = data[..Math.Min(data.Length, ElevationProtocol.MaximumPayload)];
                    ElevationProtocol.WriteFrame(pipe, channel, part);
                    data = data[part.Length..];
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _broken = true;
            return false;
        }
    }

    // The token source is not disposed: the watcher thread may still cancel it while the pipe closes.
    public void Dispose() => pipe.Dispose();
}
