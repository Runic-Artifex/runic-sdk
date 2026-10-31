namespace Runic.Desktop.Internal;

// What the WebView2 window shows while WebView2 is still starting (D053.27): the background of the window's color
// scheme, the window's title and icon, and an indeterminate progress bar. Platform-neutral so that it can be tested
// everywhere; WindowsWebView2Host paints it with GDI.
internal static class WindowsLoadingSurface
{
    // How long a starting window stays hidden before it shows the loading surface. A warm WebView2 start creates its
    // environment and controller in about 0.4 s and a cold one on a current runner image in 2 to 6 s (sdk#784), and the
    // first start after a runtime update in 13 to 78 s. 1.5 s keeps warm starts, which are most starts, as they were
    // (shown once WebView2 is ready, with no surface in between) with three times their usual time to spare, and shows
    // something before a user who waits longer starts to wonder whether the application started.
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(1500);

    // The window title until the page has a title, which is also the surface's accessible name. Runic.Desktop ships no
    // translations; the window took this title before the loading surface too.
    internal const string Title = "Loading…";

    // One sweep of the bar's moving segment across the track.
    internal const double CycleMilliseconds = 1600;

    // A COLORREF is 0x00BBGGRR.
    internal static uint Rgb(byte red, byte green, byte blue) => red | (uint)green << 8 | (uint)blue << 16;

    // The Windows 11 window backgrounds and default accent fills: white like WebView2's own default background in
    // light mode, the dark title bar's color in dark mode, so that nothing flashes white in dark mode. High contrast
    // takes the system's colors (COLOR_WINDOW, COLOR_GRAYTEXT, COLOR_HIGHLIGHT).
    internal static Palette PaletteFor(Runic.Platform.DesktopColorScheme colorScheme, bool systemDark, bool highContrast,
        Func<int, uint> systemColor)
    {
        const int ColorWindow = 5, ColorHighlight = 13, ColorGrayText = 17;
        if (highContrast)
        {
            return new Palette(systemColor(ColorWindow), systemColor(ColorGrayText), systemColor(ColorHighlight));
        }
        var dark = colorScheme switch
        {
            Runic.Platform.DesktopColorScheme.Dark => true,
            Runic.Platform.DesktopColorScheme.Light => false,
            _ => systemDark,
        };
        return dark
            ? new Palette(Rgb(0x20, 0x20, 0x20), Rgb(0x3D, 0x3D, 0x3D), Rgb(0x60, 0xCD, 0xFF))
            : new Palette(Rgb(0xFF, 0xFF, 0xFF), Rgb(0xE0, 0xE0, 0xE0), Rgb(0x00, 0x5F, 0xB8));
    }

    // The progress bar centered in a client area of the given physical size: a 4 px track (in device-independent pixels)
    // a quarter of the width, between 96 and 240 px wide, and the segment that moves across it. phase is the position
    // in the cycle from 0 to 1, or null for a still segment in the middle (animations turned off in Windows).
    internal static Bar Layout(int clientWidth, int clientHeight, uint dpi, double? phase)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var margin = (int)Math.Round(16 * scale);
        var width = Math.Clamp((int)Math.Round(clientWidth * 0.25), (int)Math.Round(96 * scale), (int)Math.Round(240 * scale));
        width = Math.Max(0, Math.Min(width, clientWidth - 2 * margin));
        var height = Math.Max(1, (int)Math.Round(4 * scale));
        var left = (clientWidth - width) / 2;
        var top = Math.Max(0, (clientHeight - height) / 2);
        var segment = (int)Math.Round(width * 0.35);
        int start;
        if (phase is { } position)
        {
            position -= Math.Floor(position);
            start = left - segment + (int)Math.Round(position * (width + segment));
        }
        else
        {
            start = left + (width - segment) / 2;
        }
        var segmentLeft = Math.Clamp(start, left, left + width);
        var segmentRight = Math.Clamp(start + segment, left, left + width);
        return new Bar(left, top, width, height, segmentLeft, segmentRight);
    }

    internal readonly record struct Palette(uint Background, uint Track, uint Indicator);

    // The track from Left to Left + Width; the segment covers SegmentLeft to SegmentRight of it.
    internal readonly record struct Bar(int Left, int Top, int Width, int Height, int SegmentLeft, int SegmentRight)
    {
        internal int Right => Left + Width;
        internal int Bottom => Top + Height;
    }
}
