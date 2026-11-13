namespace Runic.Desktop.Internal;

// A monitor's work area in physical pixels and its DPI, as WindowsMonitors reads them.
internal readonly record struct PlacementMonitor(ScreenRect WorkArea, uint Dpi);

// An open side panel: its edge and its width in native units, as SidePanelPlanner takes them.
internal readonly record struct PlacementPanel(bool Left, int NativeWidth);

// sdk#897, W1a: the arithmetic of DesktopWindow.SetPlacementAsync for Windows, where a placement mixes physical positions
// with device-independent sizes (DesktopWindowState.Placement) and GetWindowPlacement and SetWindowPlacement take the
// restored rectangle in workspace coordinates. Portable, so Linux CI tests it.
internal static class WindowPlacementMath
{
    // Workspace coordinates are screen coordinates measured from the work area of the monitor the rectangle is on: a
    // taskbar at the top or left of that monitor shifts them (GetWindowPlacement, "workspace coordinates").
    internal static ScreenRect ScreenToWorkspace(ScreenRect screen, ScreenRect monitor, ScreenRect workArea) =>
        screen with { X = screen.X - (workArea.X - monitor.X), Y = screen.Y - (workArea.Y - monitor.Y) };

    internal static ScreenRect WorkspaceToScreen(ScreenRect workspace, ScreenRect monitor, ScreenRect workArea) =>
        workspace with { X = workspace.X + (workArea.X - monitor.X), Y = workspace.Y + (workArea.Y - monitor.Y) };

    internal static int Scale(int value, uint dpi) => checked((int)Math.Round(value * (Dpi(dpi) / 96d)));

    internal static int Unscale(int value, uint dpi) => (int)Math.Max(1, Math.Round(value * 96d / Dpi(dpi)));

    // The window's restored rectangle in physical screen pixels for a placement, and the DPI its size was converted with.
    // current is the restored rectangle now, panel included, at currentDpi; a null X/Y and a zero size keep its page's.
    // The size converts with the DPI of the monitor the placement lands on, as at open (FitPlacement), and the rectangle
    // then fits that monitor's work area (WindowPlacementClamp). An open panel adds its width on its edge before the fit,
    // so the page takes the placement's bounds and the reported placement, which excludes the panel, round-trips.
    internal static (ScreenRect Restored, uint Dpi) RestoredRect(DesktopWindowPlacement placement, ScreenRect current, uint currentDpi,
        IReadOnlyList<PlacementMonitor> monitors, PlacementPanel? panel)
    {
        var page = panel is { } open ? ExcludePanel(current, open) : current;
        var x = placement.X ?? page.X;
        var y = placement.Y ?? page.Y;
        var width = placement.Width > 0 ? placement.Width : Unscale(page.Width, currentDpi);
        var height = placement.Height > 0 ? placement.Height : Unscale(page.Height, currentDpi);
        var areas = monitors.Select(static monitor => monitor.WorkArea).ToArray();
        // The monitor at the top-left corner gives the first size, as MonitorFromPoint does at open; the work area the
        // window then overlaps most gives the DPI.
        var startDpi = DpiOf(WindowPlacementClamp.Choose(new ScreenRect(x, y, 1, 1), areas), monitors) ?? currentDpi;
        var target = WindowPlacementClamp.Choose(WithPanel(new ScreenRect(x, y, Scale(width, startDpi), Scale(height, startDpi)), panel), areas);
        var dpi = DpiOf(target, monitors) ?? currentDpi;
        var rect = WithPanel(new ScreenRect(x, y, Scale(width, dpi), Scale(height, dpi)), panel);
        return (target is { } area ? WindowPlacementClamp.Clamp(rect, [area]) : rect, dpi);
    }

    private static uint? DpiOf(ScreenRect? workArea, IReadOnlyList<PlacementMonitor> monitors)
    {
        if (workArea is not { } area) return null;
        foreach (var monitor in monitors)
        {
            if (monitor.WorkArea == area) return monitor.Dpi;
        }
        return null;
    }

    // The placement the window reports for a restored rectangle: the page's, without an open panel, its size in
    // device-independent pixels at dpi.
    internal static DesktopWindowPlacement Report(ScreenRect restored, uint dpi, PlacementPanel? panel)
    {
        var page = panel is { } open ? ExcludePanel(restored, open) : restored;
        return new DesktopWindowPlacement
        {
            X = page.X, Y = page.Y, Width = Unscale(page.Width, dpi), Height = Unscale(page.Height, dpi),
            Origin = DesktopCoordinateOrigin.TopLeft,
        };
    }

    private static ScreenRect WithPanel(ScreenRect page, PlacementPanel? panel)
    {
        if (panel is not { } open) return page;
        var grown = SidePanelPlanner.IncludePanel(new DesktopWindowBounds { X = page.X, Y = page.Y, Width = page.Width, Height = page.Height },
            open.Left, open.NativeWidth);
        return new ScreenRect(grown.X!.Value, grown.Y!.Value, grown.Width, grown.Height);
    }

    private static ScreenRect ExcludePanel(ScreenRect window, PlacementPanel panel)
    {
        var page = SidePanelPlanner.ExcludePanel(new DesktopWindowBounds { X = window.X, Y = window.Y, Width = window.Width, Height = window.Height },
            panel.Left, panel.NativeWidth);
        return new ScreenRect(page.X!.Value, page.Y!.Value, page.Width, page.Height);
    }

    private static double Dpi(uint dpi) => dpi == 0 ? 96 : dpi;
}
