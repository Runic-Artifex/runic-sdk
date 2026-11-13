using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWindow.SetBoundsAsync and SetMinimumSizeAsync, and the exact, visible and snapped parts of DesktopWindowState
// (#864, B1). Bounds are GetWindowRect in physical pixels, which SetWindowPos takes back unchanged.
internal sealed partial class WindowsWebView2Host
{
    // DWMWA_EXTENDED_FRAME_BOUNDS: the frame without the invisible resize borders, in physical pixels.
    private const int DwmExtendedFrameBounds = 9;

    // Window thread only: the run-time minimum size in device-independent pixels of the outer frame, zero for none;
    // WM_GETMINMAXINFO scales it for the window's DPI. Starts from DesktopWindowOptions.MinimumWidth and MinimumHeight.
    private uint _minimumWidth;
    private uint _minimumHeight;

    // user32's IsWindowArranged (Windows 10 2004 servicing and Windows 11), or zero where it is missing.
    private static readonly Lazy<nint> IsWindowArrangedExport = new(static () =>
        NativeLibrary.TryLoad("user32.dll", out var user32) && NativeLibrary.TryGetExport(user32, "IsWindowArranged", out var export)
            ? export
            : 0);

    // One SetWindowPos: Windows moves and sizes the window in one operation, and WM_SIZE lays the WebView out within it.
    public ValueTask SetBoundsAsync(DesktopWindowBounds bounds, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_fullScreen || _options?.Kiosk == true || Native.IsZoomed(_window) || Native.IsIconic(_window))
            {
                throw WindowBoundsSupport.NotRestored();
            }
            var flags = SwpNoZOrder | SwpNoActivate;
            if (bounds.X is null) flags |= SwpNoMove;
            Native.SetWindowPos(_window, 0, bounds.X ?? 0, bounds.Y ?? 0, bounds.Width, bounds.Height, flags);
            UpdateState();
        }, cancellationToken);

    public ValueTask SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            (_minimumWidth, _minimumHeight) = ((uint)width, (uint)height);
            // A window that is smaller grows to the minimum, keeping its top-left corner. A maximized or full-screen one
            // applies it when it is restored.
            if (_fullScreen || Native.IsZoomed(_window) || Native.IsIconic(_window) || !Native.GetWindowRect(_window, out var frame)) return;
            var (currentWidth, currentHeight) = (frame.Right - frame.Left, frame.Bottom - frame.Top);
            // An open side panel adds its width (ApplyMinimumSize).
            var panelWidth = _panel is { IsShown: true } panel ? panel.NativeWidth : 0;
            var grownWidth = Math.Max(currentWidth, width == 0 ? 0 : ScaleForWindow((uint)width, _window) + panelWidth);
            var grownHeight = Math.Max(currentHeight, height == 0 ? 0 : ScaleForWindow((uint)height, _window));
            if ((grownWidth, grownHeight) != (currentWidth, currentHeight))
            {
                Native.SetWindowPos(_window, 0, 0, 0, grownWidth, grownHeight, SwpNoZOrder | SwpNoActivate | SwpNoMove);
            }
        }, cancellationToken);

    // Window thread: DesktopWindowOptions.MinimumWidth and MinimumHeight, which apply only together.
    private void InitializeMinimumSize(WebUiEmbeddedHostOptions options) =>
        (_minimumWidth, _minimumHeight) = options is { MinimumWidth: { } width, MinimumHeight: { } height } ? (width, height) : (0u, 0u);

    // Window procedure: the run-time minimum, scaled for the window's DPI. An open side panel adds its width, so the page
    // keeps the minimum width (R4); without a minimum, the system's minimum width is the page's.
    private unsafe void ApplyMinimumSize(nint window, MinMaxInfo* info)
    {
        if (_minimumWidth != 0) info->MinimumTrackSize.X = ScaleForWindow(_minimumWidth, window);
        if (_panel is { IsShown: true } panel) info->MinimumTrackSize.X += panel.NativeWidth;
        if (_minimumHeight != 0) info->MinimumTrackSize.Y = ScaleForWindow(_minimumHeight, window);
    }

    // Window thread: the exact frame, the frame the user sees and whether Snap arranged the window, for UpdateState.
    private (DesktopWindowBounds Bounds, DesktopWindowBounds Visible, bool Snapped) ReadBounds(Rect frame, uint dpi, bool maximized, bool fullScreen)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var bounds = new DesktopWindowBounds
        {
            X = frame.Left,
            Y = frame.Top,
            Width = frame.Right - frame.Left,
            Height = frame.Bottom - frame.Top,
            Scale = scale,
        };
        var visible = bounds;
        if (Native.DwmGetWindowAttribute(_window, DwmExtendedFrameBounds, out var extended, Marshal.SizeOf<Rect>()) >= 0
            && extended.Right > extended.Left && extended.Bottom > extended.Top)
        {
            visible = bounds with
            {
                X = extended.Left,
                Y = extended.Top,
                Width = extended.Right - extended.Left,
                Height = extended.Bottom - extended.Top,
            };
        }
        return (bounds, visible, !maximized && !fullScreen && IsArranged(frame));
    }

    // Window thread: IsWindowArranged, or where user32 lacks it, a window rectangle that differs from its restored one.
    private unsafe bool IsArranged(Rect frame)
    {
        if (IsWindowArrangedExport.Value is var export and not 0)
        {
            return ((delegate* unmanaged[Stdcall]<nint, int>)export)(_window) != 0;
        }
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetWindowPlacement(_window, ref placement)
            || !Native.GetMonitorInfo(Native.MonitorFromWindow(_window, MonitorDefaultToNearest), ref info))
        {
            return false;
        }
        var normal = placement.NormalPosition;
        // Workspace coordinates start at the work area's corner unless the window is a tool window.
        return WindowBoundsSupport.DiffersFromRestored(
            new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top),
            new ScreenRect(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top),
            info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
    }

    private static partial class Native
    {
        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
    }
}
