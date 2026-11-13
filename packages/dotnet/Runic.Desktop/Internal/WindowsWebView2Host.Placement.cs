using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync (sdk#897, W1a; window-api-0.19/DESIGN.md §3.1). The
// restored bounds are WINDOWPLACEMENT.rcNormalPosition in workspace coordinates, set with one SetWindowPlacement also
// while the window is maximized, minimized or hidden. A hidden window keeps its state until it is shown: ShowWindow and
// SetWindowPlacement would show it, so _maximizeWhenShown and _restoreWhenShown carry the state to SetVisibleAsync.
internal sealed partial class WindowsWebView2Host
{
    private const uint SwShowNoActivate = 4;
    private const uint SwShowMinNoActive = 7;
    private const uint SwShowNa = 8;
    private const uint WpfRestoreToMaximized = 2;

    // Window thread only: a hidden window that is maximized or minimized now and shows at its normal bounds.
    private bool _restoreWhenShown;

    // Idempotent: SW_MAXIMIZE also brings a minimized window back maximized, and activates it, as the user's maximize does.
    public ValueTask ShowMaximizedAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            if (_fullScreen)
            {
                _windowedMaximized = true;
                LeaveFullScreen();
            }
            if (!Native.IsWindowVisible(_window))
            {
                (_maximizeWhenShown, _restoreWhenShown) = (true, false);
                ReportPlacement();
                return;
            }
            if (Native.IsIconic(_window) || !Native.IsZoomed(_window)) Native.ShowWindow(_window, SwMaximize);
        }, cancellationToken);

    // Always to the normal bounds (D064.13). A visible maximized window restores without activation (SW_SHOWNOACTIVATE);
    // a minimized one comes back through RestoreFromMinimized, which Windows activates.
    public ValueTask ShowNormalAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_options?.Kiosk == true) return;
            if (_fullScreen)
            {
                _windowedMaximized = false;
                LeaveFullScreen();
            }
            _maximizeWhenShown = false;
            if (!Native.IsWindowVisible(_window))
            {
                _restoreWhenShown = Native.IsZoomed(_window) || Native.IsIconic(_window);
                ReportPlacement();
                return;
            }
            if (Native.IsIconic(_window)) RestoreFromMinimized();
            else if (Native.IsZoomed(_window)) Native.ShowWindow(_window, (int)SwShowNoActivate);
        }, cancellationToken);

    public ValueTask SetPlacementAsync(DesktopWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return InvokeAsync(() => ApplyPlacement(placement), cancellationToken);
    }

    // Window thread only: whether the window is maximized, or shows maximized when it is next shown or leaves full screen.
    private bool PlacementMaximized =>
        _fullScreen ? _windowedMaximized : _maximizeWhenShown || (Native.IsZoomed(_window) && !_restoreWhenShown);

    // Window thread only. A minimized window that was maximized restores maximized (WPF_RESTORETOMAXIMIZED); clearing the
    // flag in the placement that restores it brings it back normal. Where Windows keeps restoring it maximized, a second
    // SW_SHOWNOACTIVATE restores the maximized window, which leaves it normal either way.
    private void RestoreFromMinimized()
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (Native.GetWindowPlacement(_window, ref placement))
        {
            placement.Flags &= ~WpfRestoreToMaximized;
            placement.ShowCommand = SwShowNormal;
            Native.SetWindowPlacement(_window, ref placement);
        }
        else
        {
            Native.ShowWindow(_window, SwRestore);
        }
        if (Native.IsZoomed(_window)) Native.ShowWindow(_window, (int)SwShowNoActivate);
    }

    // Window thread only: one SetWindowPlacement with the restored rectangle and the show command for the state wanted.
    private void ApplyPlacement(DesktopWindowPlacement placement)
    {
        // A kiosk window stays full-screen; DesktopWindow refuses any other placement for it.
        if (_options?.Kiosk == true) return;
        var monitors = WindowsMonitors.TryRead();
        var panel = _panel is { IsShown: true } open ? new PlacementPanel(open.Left, open.NativeWidth) : (PlacementPanel?)null;
        var (current, currentDpi) = CurrentRestoredRect(monitors);
        var (restored, dpi) = WindowPlacementMath.RestoredRect(placement, current, currentDpi,
            [.. monitors.Select(static monitor => new PlacementMonitor(monitor.WorkArea, monitor.Dpi))], panel);
        var normal = ToWorkspace(restored, monitors);
        var reported = WindowPlacementMath.Report(restored, dpi, panel);
        if (_fullScreen)
        {
            // Full screen keeps the placement it leaves to (EnterFullScreen).
            _windowedPlacement.NormalPosition = normal;
            _windowedMaximized = placement.Maximized;
            _normal = reported;
            if (!placement.FullScreen) LeaveFullScreen();
            ReportPlacement();
            return;
        }
        var visible = Native.IsWindowVisible(_window);
        var minimized = Native.IsIconic(_window);
        var zoomed = Native.IsZoomed(_window);
        var windowPlacement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!Native.GetWindowPlacement(_window, ref windowPlacement))
        {
            throw new InvalidOperationException("Windows did not report the window's placement.");
        }
        windowPlacement.NormalPosition = normal;
        if (!visible)
        {
            // SW_HIDE keeps the window hidden and in its state; it takes the placement's state when it is shown.
            windowPlacement.ShowCommand = SwHide;
            (_maximizeWhenShown, _restoreWhenShown) = (placement.Maximized, !placement.Maximized && (zoomed || minimized));
        }
        else if (minimized)
        {
            // Stays minimized and restores to the placement's state.
            windowPlacement.ShowCommand = SwShowMinNoActive;
            windowPlacement.Flags = placement.Maximized
                ? windowPlacement.Flags | WpfRestoreToMaximized
                : windowPlacement.Flags & ~WpfRestoreToMaximized;
        }
        else if (zoomed)
        {
            // SW_SHOWNA keeps a maximized window maximized with new restored bounds, without activating it.
            windowPlacement.ShowCommand = placement.Maximized ? SwShowNa : SwShowNoActivate;
        }
        else
        {
            windowPlacement.ShowCommand = placement.Maximized ? SwShowMaximized : SwShowNa;
            // On a monitor of another scale, moving there first lets WM_DPICHANGED scale the window, so the rectangle,
            // which is already in the target monitor's pixels, is not scaled again (as MoveToMonitor does).
            if (dpi != Native.GetDpiForWindow(_window))
            {
                Native.SetWindowPos(_window, 0, restored.X, restored.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            }
        }
        Native.SetWindowPlacement(_window, ref windowPlacement);
        // The page's placement as asked, after the fit; UpdateState keeps it while the window is maximized or minimized.
        _normal = reported;
        if (placement.FullScreen && !minimized) EnterFullScreen();
        ReportPlacement();
    }

    // Window thread only: the rectangle the window restores to, in screen coordinates, and the DPI its size has: the
    // window rectangle of a normal window (also a snapped one, as State.Placement reports it), else rcNormalPosition.
    private (ScreenRect Rect, uint Dpi) CurrentRestoredRect(IReadOnlyList<WindowsMonitors.Native> monitors)
    {
        var dpi = Native.GetDpiForWindow(_window);
        if (!_fullScreen && !Native.IsZoomed(_window) && !Native.IsIconic(_window) && Native.GetWindowRect(_window, out var frame))
        {
            return (new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top), dpi);
        }
        Rect normal;
        if (_fullScreen)
        {
            normal = _windowedPlacement.NormalPosition;
        }
        else
        {
            var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            if (!Native.GetWindowPlacement(_window, ref placement)) throw new InvalidOperationException("Windows did not report the window's placement.");
            normal = placement.NormalPosition;
        }
        var workspace = new ScreenRect(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top);
        return (ToScreen(workspace, monitors), dpi);
    }

    // Workspace coordinates of the monitor that holds most of the rectangle, as GetWindowPlacement measures them.
    private static Rect ToWorkspace(ScreenRect screen, IReadOnlyList<WindowsMonitors.Native> monitors)
    {
        var workspace = MonitorOf(screen, monitors) is { } monitor
            ? WindowPlacementMath.ScreenToWorkspace(screen, monitor.Bounds, monitor.WorkArea)
            : screen;
        return new Rect { Left = workspace.X, Top = workspace.Y, Right = workspace.X + workspace.Width, Bottom = workspace.Y + workspace.Height };
    }

    private static ScreenRect ToScreen(ScreenRect workspace, IReadOnlyList<WindowsMonitors.Native> monitors) =>
        MonitorOf(workspace, monitors) is { } monitor ? WindowPlacementMath.WorkspaceToScreen(workspace, monitor.Bounds, monitor.WorkArea) : workspace;

    private static WindowsMonitors.Native? MonitorOf(ScreenRect rect, IReadOnlyList<WindowsMonitors.Native> monitors)
    {
        if (WindowPlacementClamp.Choose(rect, [.. monitors.Select(static monitor => monitor.Bounds)]) is not { } bounds) return null;
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds == bounds) return monitor;
        }
        return null;
    }

    // Window thread only: reports the placement after a change. UpdateState reads a window that is not minimized; a
    // minimized one keeps its state but reports the placement it restores to.
    private void ReportPlacement()
    {
        if (!Native.IsIconic(_window))
        {
            UpdateState();
            return;
        }
        if (Volatile.Read(ref _state) is not { } state || _normal is not { } normal) return;
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        var maximized = _maximizeWhenShown
            || (!_restoreWhenShown && Native.GetWindowPlacement(_window, ref placement) && (placement.Flags & WpfRestoreToMaximized) != 0);
        var updated = state with { Placement = normal with { Maximized = maximized, FullScreen = false } };
        if (Equals(updated, state)) return;
        Volatile.Write(ref _state, updated);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Never unwind through the window procedure.
        }
    }
}
