using System.Security.Cryptography;
using System.Text;

namespace Runic.Desktop.Internal;

// Binds a surface presented in an installed browser to the one client that opened its launch URL (SEC-01).
// The browser's command line, which other users of a Linux or macOS machine can read, carries only a one-time
// code. The first request with it is bound through the client cookie and redirected to the entry; bootstraps,
// which carry the session credential in a browser, and WebSockets are then admitted for that client alone. When
// the consumed code returns from another client, someone used it first: the binding is revoked, so neither
// client keeps the surface, and the application is told.
internal sealed class BrowserLaunchBinding
{
    internal const string PathSegment = "/_runic_launch";

    private readonly object _gate = new();
    private byte[]? _pending;
    private byte[]? _consumed;
    private Uri? _target;
    private nuint _client;
    private bool _revoked;
    private bool _active;

    internal enum Outcome
    {
        // The code was pending and is now consumed by this client.
        Bound,
        // The bound client presented the consumed code again, as on a reload.
        AlreadyBound,
        // Another client presented the consumed code: the binding is now revoked.
        Conflict,
        // The code is unknown, or the binding was revoked.
        Refused,
    }

    // Whether a browser was launched for the surface. Stays set after the browser exits, so the code it carried
    // cannot be replayed into an open surface.
    internal bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    // Starts a launch that redirects to target and returns its code. A new launch replaces an earlier code,
    // binding and revocation.
    internal string Begin(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var code = Convert.ToBase64String(OsRandom.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        lock (_gate)
        {
            _pending = Encoding.ASCII.GetBytes(code);
            _consumed = null;
            _target = target;
            _client = 0;
            _revoked = false;
            _active = true;
        }
        return code;
    }

    internal Outcome Consume(string code, nuint client, out Uri? target)
    {
        ArgumentNullException.ThrowIfNull(code);
        var offered = Encoding.UTF8.GetBytes(code);
        lock (_gate)
        {
            target = null;
            if (!_active || _revoked || client == 0)
            {
                return Outcome.Refused;
            }
            if (_pending is { } pending && CryptographicOperations.FixedTimeEquals(pending, offered))
            {
                _consumed = pending;
                _pending = null;
                _client = client;
                target = _target;
                return Outcome.Bound;
            }
            if (_consumed is { } consumed && CryptographicOperations.FixedTimeEquals(consumed, offered))
            {
                if (_client == client)
                {
                    target = _target;
                    return Outcome.AlreadyBound;
                }
                _revoked = true;
                _client = 0;
                return Outcome.Conflict;
            }
            return Outcome.Refused;
        }
    }

    // Whether a credential-bearing request from client may proceed. Always true while no browser was launched.
    internal bool Admits(nuint client)
    {
        lock (_gate)
        {
            return !_active || (!_revoked && client != 0 && client == _client);
        }
    }
}
