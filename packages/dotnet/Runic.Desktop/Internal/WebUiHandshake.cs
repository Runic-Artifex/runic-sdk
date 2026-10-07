namespace Runic.Desktop.Internal;

// The handshake state of one WebUI socket (#35). Every transition is a single
// compare-and-swap, so a token check and a deadline expiry cannot both win, and a
// socket that has ended can no longer be expired.
internal sealed class WebUiHandshake
{
    // No token check yet. Only this state can expire or end.
    public const int Pending = 0;

    // The token matched. The handshake deadline never closes this socket.
    public const int TokenChecked = 1;

    // No token check arrived in time, and the server is closing the socket.
    public const int Expired = 2;

    // The socket ended before a token check.
    public const int Ended = 3;

    // A token check failed, and the server is closing the socket.
    public const int Rejected = 4;

    private int _state;

    public int State => Volatile.Read(ref _state);

    // Expired or rejected: the socket is being closed and must not be authenticated.
    public bool IsClosing => State is Expired or Rejected;

    // Records a token check and returns the state before it. A closing or ended socket
    // stays where it is. A failed check on any live socket, including an authenticated
    // one, moves it to Rejected.
    public int CheckToken(bool tokenIsValid)
    {
        while (true)
        {
            var current = State;
            if (current is Expired or Ended or Rejected)
            {
                return current;
            }

            if (current == TokenChecked && tokenIsValid)
            {
                return current;
            }

            var target = tokenIsValid ? TokenChecked : Rejected;
            if (Interlocked.CompareExchange(ref _state, target, current) == current)
            {
                return current;
            }
        }
    }

    // Returns true only when this call moved a pending socket to Expired.
    public bool TryExpire() => Interlocked.CompareExchange(ref _state, Expired, Pending) == Pending;

    // Returns true only when this call moved a pending socket to Ended.
    public bool TryEnd() => Interlocked.CompareExchange(ref _state, Ended, Pending) == Pending;
}
