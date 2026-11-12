using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Desktop;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.Desktop.Hosting;

// The guest side of a region window (host-protocol.md, Regions): a Window whose host shows it in a region of a host
// page, in a frame, instead of a window of its own. The host attaches and detaches the region; each attach gets a new
// one-time frame launch address of the Window's surface (frame admission). What the frame's page reports of itself goes
// to the host: its title (set-title) and, for "content-height" sizing, its content height as the preferred size
// (resize). region-page tells the host about the page the frame token admitted.
internal sealed class HostedRegion : IDisposable
{
    // How long the guest waits after a capacity rejection of set-title or resize before it sends the latest value again.
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly HostedGuest _guest;
    private readonly string _window;
    private readonly DesktopSurface _surface;
    private readonly object _gate = new();
    // One region-attach at a time (host-protocol.md, region-attach: one at a time).
    private readonly SemaphoreSlim _attaching = new(1, 1);
    private readonly IDisposable _connections;
    // region-page pushes go out in the order they happened.
    private Task _pages = Task.CompletedTask;
    private bool _attached;
    private bool _measuresHeight;
    private int _width;
    private int _height;
    private double _scale = 1;
    private readonly Coalesced<string> _title;
    private readonly Coalesced<int> _preferredHeight;
    private int _disposed;

    internal HostedRegion(HostedGuest guest, string window, DesktopSurface surface)
    {
        _guest = guest;
        _window = window;
        _surface = surface;
        _title = new Coalesced<string>(title => _guest.WindowRequestAsync(_window,
            new JsonObject { ["id"] = 0, ["kind"] = "set-title", ["title"] = title }, CancellationToken.None));
        _preferredHeight = new Coalesced<int>(height =>
        {
            int width;
            lock (_gate) width = _width;
            return _guest.WindowRequestAsync(_window, new JsonObject
            {
                ["id"] = 0,
                ["kind"] = "resize",
                ["width"] = Math.Clamp(width, 1, 32_767),
                ["height"] = Math.Clamp(height, 1, 32_767),
            }, CancellationToken.None);
        });
        _surface.FrameReported += OnReported;
        _surface.FrameRevoked += OnRevoked;
        _connections = _surface.SubscribeConnectionEvents(OnConnection);
        // The page loads only through frame admission: the window-mode launch address admits nobody from now on.
        _surface.EnterRegionMode();
    }

    internal bool IsAttached
    {
        get { lock (_gate) return _attached; }
    }

    // The region's size and scale, from region-attach and region-bounds (tests).
    internal (int Width, int Height, double Scale) Bounds
    {
        get { lock (_gate) return (_width, _height, _scale); }
    }

    // region-attach: a detach of the attached region first, then a new frame launch address.
    internal async Task AttachAsync(HostRequest request)
    {
        var message = request.Message;
        if (HostProtocol.String(message, "embedding") is not { } embedding)
        {
            await request.FailAsync("rejected", "invalid-request", "The region-attach names no embedding.").ConfigureAwait(false);
            return;
        }
        if (embedding != "frame")
        {
            await request.FailAsync("rejected", "embedding-unsupported", $"This guest does not implement the embedding '{HostedWire.Shorten(embedding, 64)}'.")
                .ConfigureAwait(false);
            return;
        }
        if (Bounded(message) is not { } bounds ||
            HostProtocol.String(message, "embedder") is not { } embedder || WebUiWindow.FrameEmbedderHost(embedder) is null ||
            message["sizing"] is { } sizing && HostProtocol.String(message, "sizing") is null ||
            message["allowSameSite"] is { } allow && HostProtocol.Boolean(message, "allowSameSite") is null)
        {
            await request.FailAsync("rejected", "invalid-request", "A member of the region-attach is missing or out of range.").ConfigureAwait(false);
            return;
        }
        // An unknown sizing reads as "fill".
        var measuresHeight = HostProtocol.String(message, "sizing") == "content-height";
        var allowSameSite = HostProtocol.Boolean(message, "allowSameSite") == true;
        await _attaching.WaitAsync().ConfigureAwait(false);
        try
        {
            Detach();
            DesktopFrameLaunch launch;
            try
            {
                launch = await _surface.BeginFrameLaunchAsync(embedder, allowSameSite, measuresHeight, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DesktopFrameCrossSiteUnavailableException error)
            {
                await request.FailAsync("failed", "cross-site-unavailable", error.Message).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException)
            {
                await request.FailAsync("failed", null, "The Window is closing.").ConfigureAwait(false);
                return;
            }
            lock (_gate)
            {
                _attached = true;
                _measuresHeight = measuresHeight;
                (_width, _height, _scale) = bounds;
            }
            _title.Reset();
            _preferredHeight.Reset();
            await request.ReplyAsync(new JsonObject
            {
                ["frame"] = new JsonObject { ["url"] = launch.Url.AbsoluteUri, ["sameSite"] = launch.SameSite },
            }).ConfigureAwait(false);
        }
        finally
        {
            _attaching.Release();
        }
    }

    // region-detach, and a detach before a new attach: the code and token are revoked and the page disconnected. The
    // Window stays open.
    internal void Detach()
    {
        lock (_gate)
        {
            if (!_attached) return;
            _attached = false;
        }
        _surface.EndFrameLaunch();
    }

    // region-bounds: the region's new size and scale; one out of range is ignored.
    internal void OnBounds(JsonObject push)
    {
        if (Bounded(push) is not { } bounds) return;
        lock (_gate)
        {
            if (_attached) (_width, _height, _scale) = bounds;
        }
    }

    // region-focus enter: the frame's page focuses its first, last or last focused element.
    internal void OnFocus(JsonObject push)
    {
        if (HostProtocol.String(push, "focus") != "enter" || HostProtocol.String(push, "direction") is not
            ({ } direction and ("forward" or "backward" or "programmatic")) || !IsAttached)
            return;
        _ = FocusAsync(direction);
    }

    private async Task FocusAsync(string direction)
    {
        try { await _surface.FocusFrameAsync(direction, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or IOException
            or System.Net.WebSockets.WebSocketException) { }
    }

    // width, height from 0 to 32,767 and scale from 0.25 to 8, or null.
    private static (int, int, double)? Bounded(JsonObject message) =>
        HostProtocol.Integer(message, "width") is >= 0 and <= 32_767 and var width &&
        HostProtocol.Integer(message, "height") is >= 0 and <= 32_767 and var height &&
        message["scale"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        value.TryGetValue<double>(out var scale) && scale is >= 0.25 and <= 8
            ? ((int)width, (int)height, scale)
            : null;

    // What the frame's page reported, as WebUiWindow raises it; the conformance runner's setTitle, whose pages are not
    // loaded, reports it directly.
    internal void Report(DesktopFrameReport report) => OnReported(report);

    private void OnReported(DesktopFrameReport report)
    {
        bool measuresHeight;
        lock (_gate)
        {
            if (!_attached) return;
            measuresHeight = _measuresHeight;
        }
        // At most 1,024 characters without NUL (host-protocol.md, set-title).
        if (report.Title is { } title) _title.Send(HostedWire.Shorten(title.Replace("\0", "", StringComparison.Ordinal), 1_024));
        if (measuresHeight && report.Height is { } height) _preferredHeight.Send(height);
    }

    private void OnConnection(DesktopConnectionEvent connection) =>
        Page(connection.Kind == DesktopConnectionEventKind.Connected ? "connected" : "disconnected");

    private void OnRevoked() => Page("revoked");

    // region-page, only while attached, in order.
    private void Page(string page)
    {
        lock (_gate)
        {
            if (!_attached || Volatile.Read(ref _disposed) != 0) return;
            _pages = _pages.ContinueWith(_ => _guest.PushAsync(new JsonObject { ["kind"] = "region-page", ["window"] = _window, ["page"] = page }),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _surface.FrameReported -= OnReported;
        _surface.FrameRevoked -= OnRevoked;
        _connections.Dispose();
        lock (_gate) _attached = false;
        _title.Dispose();
        _preferredHeight.Dispose();
    }

    // The latest value of a request that the guest sends whenever its page reports a change: one request at a time,
    // the latest value wins, the same value is not sent twice, and a capacity rejection sends the latest value again
    // after RetryDelay.
    private sealed class Coalesced<T>(Func<T, Task<JsonObject?>> send) : IDisposable where T : notnull
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _disposed = new();
        private bool _hasPending;
        private T _pending = default!;
        private bool _hasSent;
        private T _sent = default!;
        private bool _sending;

        // A new attachment sends its page's values again.
        internal void Reset()
        {
            lock (_gate) _hasSent = false;
        }

        internal void Send(T value)
        {
            lock (_gate)
            {
                if (_disposed.IsCancellationRequested) return;
                _pending = value;
                _hasPending = true;
                if (_sending) return;
                _sending = true;
            }
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            while (true)
            {
                T value;
                lock (_gate)
                {
                    if (!_hasPending || _disposed.IsCancellationRequested)
                    {
                        _sending = false;
                        return;
                    }
                    value = _pending;
                    _hasPending = false;
                    if (_hasSent && EqualityComparer<T>.Default.Equals(_sent, value)) continue;
                }
                JsonObject? reply;
                try { reply = await send(value).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { reply = null; }
                var capacity = reply is not null && HostProtocol.Boolean(reply, "ok") != true &&
                    reply["error"] is JsonObject failure && HostProtocol.String(failure, "reason") == "capacity";
                lock (_gate)
                {
                    if (capacity)
                    {
                        // The latest value goes again, unless a newer one arrived meanwhile.
                        if (!_hasPending)
                        {
                            _pending = value;
                            _hasPending = true;
                        }
                    }
                    else
                    {
                        _sent = value;
                        _hasSent = true;
                    }
                }
                if (capacity)
                {
                    try { await Task.Delay(RetryDelay, _disposed.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        public void Dispose() => _disposed.Cancel();
    }
}
