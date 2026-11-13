namespace Runic.Desktop.Internal;

// sdk#864, P4: how a GTK window's content divides between its page and its side panel (side-panel/DESIGN.md §1.2, §1.7).
// Both views are children of an overlay that positions them from its allocated width, so neither view sizes the window.
// A panel change requests new window bounds and keeps the old layout until the first allocation with the requested
// width, then commits the new one: the page keeps its width throughout, and on the left edge the window manager's move
// and the page's offset arrive together (the spike's "layout follows the configured size"; 0 page resizes in 21/21
// transitions). A move to the other edge keeps the window's width, so it commits when the window has moved (CommitMove).
// A window manager that does not grant the requested width would leave a change pending; the host commits it after a
// timeout (Commit). GTK thread only; shared by the GTK 3 host and Runic.Desktop.Gtk4, which compiles this file too.
internal sealed class SidePanelLayout
{
    private Phase _phase;
    private bool _left;
    private int _width;
    // The change waiting for its allocation or move: the content width it waits for, and the layout it commits.
    private int _target;
    private bool _nextLeft;
    private int _nextWidth;
    private TaskCompletionSource? _committed;

    private enum Phase { None, Loading, Opening, Open, Changing, Moving, Closing }

    // Whether the panel takes part in the layout: shown beside the page, or about to leave it.
    internal bool IsShown => _phase is Phase.Open or Phase.Changing or Phase.Moving or Phase.Closing;

    // Whether a panel view exists, loading or shown.
    internal bool HasPanel => _phase != Phase.None;

    internal bool IsLeft => _left;

    // The panel's width in the layout now.
    internal int Width => _width;

    // A change waits for its allocation or move.
    internal bool IsPending => _committed is not null;

    // The panel's view exists and loads outside the content, where the overlay clips it.
    internal void BeginLoading(bool left, int width)
    {
        _phase = Phase.Loading;
        (_left, _width) = (left, width);
    }

    // The loading panel went away without opening.
    internal void Discard()
    {
        _phase = Phase.None;
        Complete();
    }

    // Opening grows the content from contentWidth by width: the panel shows at the first allocation that wide.
    internal Task BeginOpen(int contentWidth, bool left, int width) =>
        Begin(Phase.Opening, contentWidth + width, left, width);

    // Changing the width moves the docked edge by the difference.
    internal Task BeginResize(int contentWidth, int width) =>
        Begin(Phase.Changing, contentWidth + width - _width, _left, width);

    // Moving keeps the content's width; CommitMove applies it once the window moved.
    internal Task BeginMove(bool left) => Begin(Phase.Moving, -1, left, _width);

    // Closing shrinks the content by the panel's width; the page takes all of it at the first allocation that narrow.
    internal Task BeginClose(int contentWidth) => Begin(Phase.Closing, Math.Max(1, contentWidth - _width), _left, _width);

    // A window whose frame does not change for the panel (maximized, full screen): the change applies at once.
    internal void CloseNow()
    {
        _phase = Phase.None;
        Complete();
    }

    // The overlay's allocation: commits a change waiting for this width, then lays out. Returns the page's and the panel's
    // horizontal place; a panel outside the layout (loading, closed) is placed just outside the content.
    internal (int PageX, int PageWidth, int PanelX, int PanelWidth) Allocate(int contentWidth)
    {
        if (_phase is Phase.Opening or Phase.Changing or Phase.Closing && contentWidth == _target) Commit();
        return Layout(contentWidth);
    }

    // The window moved to the target of a move.
    internal void CommitMove()
    {
        if (_phase == Phase.Moving) Commit();
    }

    // Applies the pending change, whatever the window's width: after a timeout, or when the window cannot change.
    internal void Commit()
    {
        switch (_phase)
        {
            case Phase.Opening:
            case Phase.Changing:
            case Phase.Moving:
                _phase = Phase.Open;
                (_left, _width) = (_nextLeft, _nextWidth);
                break;
            case Phase.Closing:
                _phase = Phase.None;
                break;
        }
        Complete();
    }

    internal (int PageX, int PageWidth, int PanelX, int PanelWidth) Layout(int contentWidth)
    {
        contentWidth = Math.Max(0, contentWidth);
        return _phase switch
        {
            Phase.Open or Phase.Changing or Phase.Moving or Phase.Closing => SidePanelPlanner.Layout(contentWidth, _left, _width),
            // Loading, opening: the page takes the content, the panel waits just outside it.
            _ => (0, contentWidth, contentWidth, Math.Max(1, _width)),
        };
    }

    private Task Begin(Phase phase, int target, bool left, int width)
    {
        // A change still pending applies first, so its caller completes.
        if (_committed is not null) Commit();
        _phase = phase;
        _target = target;
        (_nextLeft, _nextWidth) = (left, width);
        _committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _committed.Task;
    }

    private void Complete()
    {
        var committed = _committed;
        _committed = null;
        committed?.TrySetResult();
    }
}
