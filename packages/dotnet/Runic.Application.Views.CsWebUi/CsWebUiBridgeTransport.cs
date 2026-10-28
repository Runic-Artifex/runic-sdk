using System.Globalization;
using System.Text;
using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Creates Bridge transports for CS-WebUI windows.</summary>
public static class CsWebUiBridgeExtensions
{
    /// <summary>Creates the Views transport of <paramref name="window"/>.</summary>
    /// <remarks>
    /// The transport binds one function, <c>__runicViews</c>, which <c>runic-cswebui.js</c> calls with each request,
    /// and the window's all-events binding, through which it learns of closed connections. CS-WebUI cannot remove a
    /// binding before its window closes, so the binding stays while the window's request handler changes; a request
    /// while none is served gets the <c>released</c> reply. The returned object is also <see cref="IDisposable"/>:
    /// dispose it before disposing <paramref name="window"/>.
    /// </remarks>
    public static IBridgeTransport CreateBridgeSession(this WebUiWindow window) => new CsWebUiBridgeTransport(window);
}

// The Views protocol over CS-WebUI (specs/application/README.md, "Transport"):
//
// - Requests: one binding, __runicViews, with the request's JSON as its argument. The trusted client key is WebUI's
//   client id and the connection key clientId:connectionId.
// - Broadcast: raw data to every client of the window (SendRaw), the message only; runic-cswebui.js hands its text to
//   the page's push listeners.
// - Targeted pushes: WebUI cannot send to one client outside an event, so this transport does not implement
//   IBridgeConnectionTransport. The window's dispatcher queues them in the connection's mailbox, and runic-cswebui.js
//   drains it with receive requests, at most one pending per connection. Each pending receive holds one native WebUI
//   thread for up to 25 s; WebUI runs every call on its own thread, so it does not hold up the client's other calls.
// - Closed connections: the window's all-events binding reports disconnects. With it WebUI also cancels the page's
//   navigations until a Bridge call allows them and reports each one; the transport asks runic-cswebui.js to repeat
//   it (#196).
// - Order: WebUI runs each call on its own thread, so it does not keep a client's admission order; edits carry their
//   own numbers. A broadcast sent right after a reply can overtake it, which the protocol allows.
internal sealed class CsWebUiBridgeTransport : IBridgeTransport, IDisposable
{
    // The binding runic-cswebui.js calls with one request.
    internal const string RequestBinding = "__runicViews";

    // The raw-data function of runic-cswebui.js that receives pushes.
    internal const string PushFunction = "__runicViewsPush";

    // runic-cswebui.js records each navigation WebUI cancels; this function repeats the last one with navigation
    // allowed. It is called with raw data, not a script, so the page's policy needs no 'unsafe-eval'. WebUI sends
    // nothing for empty data, so the call carries one ignored byte.
    internal const string ContinueNavigationFunction = "__runicContinueNavigation";
    private static ReadOnlySpan<byte> ContinueNavigationData => [0];

    private readonly object _gate = new();
    private readonly WebUiWindow _window;
    private readonly WebUiBinding _requests;
    private readonly WebUiBinding _events;
    private IBridgeRequestHandler? _handler;
    private bool _disposed;

    internal CsWebUiBridgeTransport(WebUiWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _requests = window.BindAsync(RequestBinding, HandleAsync);
        try
        {
            // Only the all-events binding receives disconnects. runic-cswebui.js stops WebUI from also sending click
            // events for elements with ids.
            _events = window.Bind("", OnEvent);
        }
        catch
        {
            _requests.Dispose();
            throw;
        }
    }

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handler is not null) throw new InvalidOperationException("The transport already serves a request handler.");
            _handler = handler;
        }
        return new ServeLease(this, handler);
    }

    // One uncoalesced WebSocket write per call to every connected client of the window, so pushes keep their order.
    // WebUI blocks until each socket write completes: TCP-level back-pressure, not acknowledgement. Since WebUI
    // 9264f7ae9 (webui#723, CsWebUi 2.5.0-beta.4.8) only this window's clients wait. The push is raw data
    // (webui_send_raw), which needs no 'unsafe-eval' in the page's Content-Security-Policy, unlike a script.
    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_disposed) return ValueTask.CompletedTask;
        // The window can be disposed right after this transport, while the push is being sent.
        try { _window.SendRaw(PushFunction, message.Span); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops serving and removes the bindings. Dispose this transport before its window.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handler = null;
        }
        try { _requests.Dispose(); }
        finally { _events.Dispose(); }
    }

    // CS-WebUI reads each argument with the length WebUI received and throws InvalidDataException for a call whose
    // argument claims more than WebUI's message limit; that is a request over the input limit.
    private async ValueTask<WebUiResult> HandleAsync(WebUiEvent e, CancellationToken cancellationToken)
    {
        IBridgeRequestHandler? handler;
        lock (_gate) handler = _disposed ? null : _handler;
        if (handler is null) return WebUiResult.FromString(Encoding.UTF8.GetString(BridgeMessages.Released.Span));
        byte[] message;
        try { message = e.GetBytes(0); }
        catch (InvalidDataException)
        {
            return WebUiResult.FromString(Encoding.UTF8.GetString(
                BridgeMessages.Rejected("invalid-input", "The request is larger than the host's message limit.")));
        }
        var connection = ConnectionKey(e);
        var reply = await handler.HandleAsync(
            new BridgeRequest(message, e.ClientId.ToString(CultureInfo.InvariantCulture), connection),
            cancellationToken).ConfigureAwait(false);
        return WebUiResult.FromString(Encoding.UTF8.GetString(reply.Span));
    }

    private void OnEvent(WebUiEvent e)
    {
        if (e.EventType == WebUiEventType.Disconnected)
        {
            IBridgeRequestHandler? handler;
            lock (_gate) handler = _handler;
            handler?.ConnectionClosed(ConnectionKey(e));
        }
        // With this binding, WebUI cancels the page's navigations from each (re)connect until runic-cswebui.js allows
        // them before a Bridge call, and reports each one here; ignoring it dropped a reload or a link before the first
        // call (#196). Since WebUI 597243906 (webui#724) the event carries the destination URL, but not the
        // navigation's type (reload, traversal, replace) or form data, and only the page can repeat it, so the page
        // repeats the navigation it recorded, if it stays on its origin.
        else if (e.EventType == WebUiEventType.Navigation)
            e.SendRaw(ContinueNavigationFunction, ContinueNavigationData);
    }

    internal static string ConnectionKey(WebUiEvent e) =>
        string.Create(CultureInfo.InvariantCulture, $"{e.ClientId}:{e.ConnectionId}");

    private sealed class ServeLease(CsWebUiBridgeTransport owner, IBridgeRequestHandler handler) : IDisposable
    {
        private CsWebUiBridgeTransport? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } transport) return;
            lock (transport._gate)
                if (ReferenceEquals(transport._handler, handler)) transport._handler = null;
        }
    }
}
