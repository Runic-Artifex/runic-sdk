namespace Runic.Desktop.Internal;

// A frame a window restores to, in one host's units: the position, null where the platform gives none (Wayland), and
// the size.
internal readonly record struct RestoreFrame(int? X, int? Y, int Width, int Height);

// What a host does after a report of its window (WindowRestoreBounds.Observe).
internal readonly record struct RestoreObservation(RestoreFrame? Restore, RestoreFrame? Apply);

// sdk#897, W1a: the bounds a window restores to, for the hosts that keep them themselves (WKWebView, GTK 3, GTK 4;
// WebView2 keeps them in the window's WINDOWPLACEMENT). Linked into Runic.Desktop.Gtk4; portable, so Linux CI tests it.
//
// The hosts used to infer the restore bounds from what the window reported while it was normal, and relied on AppKit or
// the window manager to return a maximized window to them. Both failed in CI: a window reported on its way to or from
// the maximized frame recorded that frame, and AppKit and Openbox returned to a frame they had saved earlier, after a
// later placement had replaced it. Now the bounds a placement or a maximize states are held until the window is normal
// at them:
// - Set: SetPlacementAsync (normal or maximized) and MaximizeAsync of a normal window state the bounds. A later Set
//   replaces them; an earlier one never comes back.
// - While held, reports of the window do not replace them. A window that was maximized since and is normal at other
//   bounds (the window manager's, AppKit's) is given them again (Apply), a few times.
// - Released when the window is normal at them, after the tries, or when a window that was not maximized meanwhile is
//   normal elsewhere for longer than HoldTime: the user moved it, and its frame is the restore bounds from then on.
internal sealed class WindowRestoreBounds
{
    internal static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(3);
    internal const int MaximumTries = 5;
    // The bounds are given again at most this often: a burst of reports while the window manager has not processed the
    // last request yet would use up the tries at once.
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    private RestoreFrame? _held;
    private RestoreFrame? _observed;
    private bool _seenMaximized;
    private int _tries;
    private TimeSpan _since;
    private TimeSpan? _lastApply;

    // The bounds the window restores to: the held ones, else the last normal frame reported.
    internal RestoreFrame? Current => _held ?? _observed;

    internal bool IsHeld => _held is not null;

    // maximized: whether the window is maximized now, so it is given the bounds once it is back from it.
    internal void Set(RestoreFrame bounds, TimeSpan now, bool maximized = false) =>
        (_held, _seenMaximized, _tries, _since, _lastApply) = (bounds, maximized, 0, now, null);

    // The last frame a host reported while normal, without holding it (a window that opened at it).
    internal void Reset(RestoreFrame? bounds) => (_held, _observed, _seenMaximized, _tries) = (null, bounds, false, 0);

    // A report of the window: whether it is maximized or full-screen, and its frame while it is normal and shown (null
    // while it is maximized, full-screen, minimized or hidden, or in transit as far as the host can tell).
    internal RestoreObservation Observe(bool maximized, RestoreFrame? normalFrame, TimeSpan now)
    {
        if (maximized)
        {
            _seenMaximized = true;
            return new(Current, null);
        }
        if (normalFrame is not { } frame) return new(Current, null);
        if (_held is not { } held)
        {
            _observed = frame;
            return new(frame, null);
        }
        if (Matches(frame, held))
        {
            (_held, _observed) = (null, frame);
            return new(frame, null);
        }
        if (_seenMaximized)
        {
            // Back from maximized at the window manager's or AppKit's frame: give it the held bounds.
            if (_lastApply is { } last && now - last < RetryInterval) return new(held, null);
            if (_tries++ < MaximumTries)
            {
                _lastApply = now;
                return new(held, held);
            }
            (_held, _observed) = (null, frame);
            return new(frame, null);
        }
        if (now - _since > HoldTime)
        {
            (_held, _observed) = (null, frame);
            return new(frame, null);
        }
        // In transit to the held bounds.
        return new(held, null);
    }

    // A position the platform does not report (Wayland) matches any.
    private static bool Matches(RestoreFrame frame, RestoreFrame held) =>
        frame.Width == held.Width && frame.Height == held.Height && (held.X is null || frame.X == held.X && frame.Y == held.Y);
}
