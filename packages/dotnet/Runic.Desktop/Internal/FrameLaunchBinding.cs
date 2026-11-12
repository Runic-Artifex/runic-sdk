using System.Security.Cryptography;
using System.Text;

namespace Runic.Desktop.Internal;

// Admits the page of a surface that a host page shows in a frame (runic-host/1 region/1, frame admission). A frame uses
// no cookie: in a frame of the host's site a client cookie would land in the host's cookie jar, and a cross-site frame
// gets none in WebKit. Instead the host's backend receives a one-time frame launch code over the authenticated host
// channel; the frame's first request with it is answered with a redirect whose URL fragment carries a frame token. A
// fragment never reaches a server or a Referer, the host page cannot read a cross-origin frame's location or storage,
// and the code is spent by the frame's own navigation, so only the framed page holds the token. Its bootstrap keeps the
// token in sessionStorage, for reloads, and offers it on the Bridge WebSocket upgrade in place of the session
// credential (Sec-WebSocket-Protocol: runic-desktop.<token>), which never leaves the process in region mode. The
// surface admits one connected page at a time (single client), so a copy of the token is useless while the frame is
// connected, and it dies with the attachment. A spent code presented again means that someone else used it first: the
// token is revoked, so neither keeps the page.
internal sealed class FrameLaunchBinding
{
    internal const string PathSegment = "/_runic_frame";
    // The fragment of the redirect: #runic-frame=<token>. The bootstrap scripts read it (WebUiWindow).
    internal const string FragmentName = "runic-frame";
    // An unused code expires this long after it was minted.
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private bool _region;
    private byte[]? _pending;
    private byte[]? _consumed;
    private long _expires;
    private string? _token;
    private Uri? _target;
    private bool _revoked;
    private string? _embedder;
    private Uri? _listener;
    private bool _measuresHeight;
    private int _generation;

    internal enum Outcome
    {
        // The code was pending and is now spent: the redirect carries the token.
        Bound,
        // The spent code came again: the token is now revoked.
        Conflict,
        // The code is unknown or expired, or no attachment admits it.
        Refused,
    }

    // Whether the surface is in region mode: its page loads only in a host page's frame, from the first Enter or Begin on.
    internal bool IsActive
    {
        get { lock (_gate) return _region; }
    }

    // The one origin that may frame the surface (frame-ancestors) while attached, else null: 'none'.
    internal string? Embedder
    {
        get { lock (_gate) return _embedder; }
    }

    // The base URL of the listener that serves the frame (PresentationHostCore) while attached.
    internal Uri? Listener
    {
        get { lock (_gate) return _listener; }
    }

    // Whether the page reports its content height (region-attach's sizing "content-height") while attached.
    internal bool MeasuresHeight
    {
        get { lock (_gate) return _measuresHeight; }
    }

    // Changes whenever the admitted token does: at each attach, detach and launch conflict. A page admitted under an
    // earlier generation is disconnected (WebUiWindow.AcceptWebSocketAsync).
    internal int Generation
    {
        get { lock (_gate) return _generation; }
    }

    // Region mode without an attachment: no frame is admitted, and no origin may frame the surface.
    internal void Enter()
    {
        lock (_gate) _region = true;
    }

    // Attaches: a new code for a frame of embedder, served from listener, that redirects to target. It replaces an
    // earlier code and token, which are revoked.
    internal string Begin(Uri target, string embedder, Uri listener, bool measuresHeight, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(time);
        var code = NewSecret();
        lock (_gate)
        {
            _region = true;
            _generation++;
            _pending = Encoding.ASCII.GetBytes(code);
            _consumed = null;
            _expires = time.GetTimestamp() + (long)(CodeLifetime.TotalSeconds * time.TimestampFrequency);
            _token = NewSecret();
            _target = target;
            _revoked = false;
            _embedder = embedder;
            _listener = listener;
            _measuresHeight = measuresHeight;
        }
        return code;
    }

    // Detaches: the code and the token are revoked; the surface stays in region mode. False when it was not attached.
    internal bool End()
    {
        lock (_gate)
        {
            var attached = _embedder is not null;
            _generation++;
            _pending = null;
            _consumed = null;
            _token = null;
            _target = null;
            _revoked = false;
            _embedder = null;
            _listener = null;
            _measuresHeight = false;
            return attached;
        }
    }

    internal Outcome Consume(string code, TimeProvider time, out string? token, out Uri? target)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(time);
        var offered = Encoding.UTF8.GetBytes(code);
        lock (_gate)
        {
            token = null;
            target = null;
            if (_embedder is null || _revoked) return Outcome.Refused;
            if (_pending is { } pending && CryptographicOperations.FixedTimeEquals(pending, offered))
            {
                _pending = null;
                // An expired code admits nobody, now or later.
                if (time.GetTimestamp() >= _expires) return Outcome.Refused;
                _consumed = pending;
                token = _token;
                target = _target;
                return Outcome.Bound;
            }
            if (_consumed is { } consumed && CryptographicOperations.FixedTimeEquals(consumed, offered))
            {
                _revoked = true;
                _token = null;
                _generation++;
                return Outcome.Conflict;
            }
            return Outcome.Refused;
        }
    }

    // The WebSocket subprotocol a page that holds the token offers (runic-desktop.<token>), or null when no page can be
    // admitted: before the code was spent, after a conflict, and while not attached.
    internal string? ExpectedProtocol
    {
        get
        {
            lock (_gate)
            {
                return !_revoked && _consumed is not null && _token is { } token ? "runic-desktop." + token : null;
            }
        }
    }

    // 32 random bytes, base64url without padding: 43 characters.
    private static string NewSecret() =>
        Convert.ToBase64String(OsRandom.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Replaces the code of a launch path and the token of a frame fragment in text that may be logged.
    internal static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var marker in (ReadOnlySpan<string>)[PathSegment + "/", BrowserLaunchBinding.PathSegment + "/", "#" + FragmentName + "="])
        {
            var start = 0;
            while ((start = text.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
            {
                var secret = start + marker.Length;
                var end = secret;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '-' or '_')) end++;
                if (end > secret) text = string.Concat(text.AsSpan(0, secret), "…", text.AsSpan(end));
                start = secret;
            }
        }
        return text;
    }
}

// A frame launch address (FrameLaunchBinding), and whether it fell back to the host page's own site.
internal sealed record DesktopFrameLaunch(Uri Url, bool SameSite);

// What a frame's page reported of itself: its title, its content height in CSS pixels, or both.
internal sealed record DesktopFrameReport(string? Title, int? Height);

// The page that a frame token admitted (runic-host/1 region-page).
internal enum DesktopFramePage
{
    Connected,
    Disconnected,
    Revoked,
}

// A frame cannot be served cross-site to its host page, and the host did not allow the same-site fallback
// (runic-host/1 cross-site-unavailable).
internal sealed class DesktopFrameCrossSiteUnavailableException(string message) : InvalidOperationException(message);
