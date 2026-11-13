namespace Runic.Desktop.Internal;

// Admits the component of a surface that a host page loads into its own document (runic-host/1 region-component/1,
// component admission). The component's module and assets are served to the embedder with CORS (WebUiWindow's
// ComponentPath); its Bridge WebSocket comes from the host page's own origin, so neither a cookie nor the frame's
// fragment token can admit it. Instead the host's backend receives a component token over the authenticated host
// channel and hands it to its page, which gives it to the component; the component offers it as the WebSocket
// subprotocol (Sec-WebSocket-Protocol: runic-desktop.<token>), and the surface admits the upgrade only when its Origin is
// the embedder as well. The two checks cover different requesters: a browser cannot forge Origin, so another page of
// any origin is refused even if it obtained the token; a process that is not a browser can send any Origin, so it needs
// the token, which only the host's backend and its page hold. The surface admits one connected page at a time, and the
// token dies with the attachment.
internal sealed class ComponentBinding
{
    private readonly object _gate = new();
    private string? _embedder;
    private string? _token;
    private Uri? _listener;
    private int _generation;

    // The origin of the host page that loads the component while attached, as browsers serialize it, else null.
    internal string? Embedder
    {
        get { lock (_gate) return _embedder; }
    }

    // The base URL of the listener that serves the component (its surface URL on that listener) while attached.
    internal Uri? Listener
    {
        get { lock (_gate) return _listener; }
    }

    // Changes whenever the admitted token does: at each attach and detach. A page admitted under an earlier generation
    // is disconnected (WebUiWindow.AcceptWebSocketAsync).
    internal int Generation
    {
        get { lock (_gate) return _generation; }
    }

    // Attaches: a new token for a component of embedder, served from listener. It replaces an earlier token.
    internal string Begin(string embedder, Uri listener)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(listener);
        var token = NewSecret();
        lock (_gate)
        {
            _generation++;
            _embedder = embedder;
            _listener = listener;
            _token = token;
        }
        return token;
    }

    // Detaches: the token is revoked. False when it was not attached.
    internal bool End()
    {
        lock (_gate)
        {
            var attached = _embedder is not null;
            _generation++;
            _embedder = null;
            _listener = null;
            _token = null;
            return attached;
        }
    }

    // The WebSocket subprotocol a component that holds the token offers (runic-desktop.<token>), or null while not
    // attached.
    internal string? ExpectedProtocol
    {
        get
        {
            lock (_gate) return _token is { } token ? "runic-desktop." + token : null;
        }
    }

    // 32 random bytes, base64url without padding: 43 characters.
    private static string NewSecret() =>
        Convert.ToBase64String(OsRandom.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// Where a component's Bridge connects (runic-host/1 region-component/1): the surface's base URL on the listener that
// serves it, the component token, and whether that listener is of the host page's own site.
internal sealed record DesktopComponentLaunch(Uri Listener, string Token, bool SameSite);

