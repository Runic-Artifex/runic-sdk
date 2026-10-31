using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The loading surface (D053.27, WindowsLoadingSurface): a window whose WebView2 environment and controller are not
// ready after the grace period is shown anyway, with the same placement, size, maximized or full-screen state and DPI
// as when it is shown ready, and paints the surface until the WebView is created and covers it. A start within the grace
// period shows the window once WebView2 is ready, as before. A hidden window never shows the surface.
internal sealed partial class WindowsWebView2Host
{
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmTimer = 0x0113;
    // Destroys a window that is still opening and that its caller gave up (cancelled or disposed).
    private const uint WmAppAbandon = 0x8002;
    private const nuint GraceTimer = 1;
    private const nuint AnimationTimer = 2;
    private const uint AnimationFrameMilliseconds = 33;

    // Test seams: the grace period, and a delay before the WebView2 environment is created, which stands for a slow
    // first start. Process-wide; the smokes set them around the windows they open.
    internal static TimeSpan LoadingSurfaceDelay { get; set; } = WindowsLoadingSurface.GracePeriod;
    internal static Func<Task>? BeforeEnvironmentCreation { get; set; }
    // Called on the window thread when a window starts (true) and stops (false) showing the loading surface.
    internal static Action<nint, bool>? LoadingSurfaceChanged { get; set; }

    // Window thread only.
    private bool _shown;
    private bool _loading;
    private bool _loadingAnimated;
    private bool _closedWhileLoading;
    private long _loadingStarted;
    private WindowsLoadingSurface.Palette _palette;
    private uint _threadId;
    private Task? _initialization;

    // Run, before the message loop: the window shows the surface if WebView2 is not ready after the grace period.
    private void ScheduleLoadingSurface(WebUiEmbeddedHostOptions options)
    {
        if (options.Hidden) return;
        var delay = (uint)Math.Clamp(LoadingSurfaceDelay.TotalMilliseconds, 0, int.MaxValue);
        Native.SetTimer(_window, GraceTimer, Math.Max(1u, delay), 0);
    }

    private void ShowLoadingSurface()
    {
        Native.KillTimer(_window, GraceTimer);
        if (_shown || IsOpen || _window == 0) return;
        if (_ready.Task.IsCompleted || Volatile.Read(ref _disposed) != 0)
        {
            // The caller cancelled or let go of this window before the grace period ended.
            Native.DestroyWindow(_window);
            return;
        }
        var options = _options!;
        _palette = WindowsLoadingSurface.PaletteFor(options.ColorScheme, SystemUsesDarkTheme(), options.HighContrast, Native.GetSysColor);
        _loadingAnimated = ClientAreaAnimations();
        _loadingStarted = Stopwatch.GetTimestamp();
        _loading = true;
        options.StartupProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"WebView2 not ready after {LoadingSurfaceDelay.TotalMilliseconds:0} ms; the window shows its loading surface"));
        ShowInitially();
        if (_loadingAnimated) Native.SetTimer(_window, AnimationTimer, AnimationFrameMilliseconds, 0);
        LoadingSurfaceChanged?.Invoke(_window, true);
    }

    // Once the WebView exists it covers the client area, and the window paints nothing of its own again.
    private void EndLoadingSurface()
    {
        Native.KillTimer(_window, GraceTimer);
        if (!_loading) return;
        _loading = false;
        Native.KillTimer(_window, AnimationTimer);
        LoadingSurfaceChanged?.Invoke(_window, false);
    }

    // The first show, after the grace period or once WebView2 is ready: the same state either way.
    private void ShowInitially()
    {
        _shown = true;
        var options = _options!;
        var maximize = options.Placement is { Maximized: true } && !options.Kiosk;
        if (options.Kiosk)
        {
            Native.ShowWindow(_window, SwMaximize);
        }
        else if (options.Hidden)
        {
            Native.ShowWindow(_window, SwHide);
            _maximizeWhenShown = maximize;
        }
        else
        {
            Native.ShowWindow(_window, maximize ? SwMaximize : SwShow);
        }
        // A placement saved full-screen opens full-screen on the monitor of its restored bounds.
        if (options.Placement is { FullScreen: true } && !options.Kiosk)
        {
            EnterFullScreen();
        }
        Native.UpdateWindow(_window);
    }

    private WindowsLoadingSurface.Bar LoadingBar()
    {
        Native.GetClientRect(_window, out var client);
        double? phase = _loadingAnimated
            ? Stopwatch.GetElapsedTime(_loadingStarted).TotalMilliseconds / WindowsLoadingSurface.CycleMilliseconds
            : null;
        return WindowsLoadingSurface.Layout(client.Right, client.Bottom, Native.GetDpiForWindow(_window), phase);
    }

    private unsafe void PaintLoadingSurface()
    {
        var dc = Native.BeginPaint(_window, out var paint);
        if (dc == 0) return;
        try
        {
            // The bar first, then the background around it, so that no pixel is painted twice in a frame.
            var bar = LoadingBar();
            if (bar.Width > 0)
            {
                Fill(dc, bar.Left, bar.Top, bar.SegmentLeft, bar.Bottom, _palette.Track);
                Fill(dc, bar.SegmentLeft, bar.Top, bar.SegmentRight, bar.Bottom, _palette.Indicator);
                Fill(dc, bar.SegmentRight, bar.Top, bar.Right, bar.Bottom, _palette.Track);
                _ = Native.ExcludeClipRect(dc, bar.Left, bar.Top, bar.Right, bar.Bottom);
            }
            Fill(dc, paint.Paint.Left, paint.Paint.Top, paint.Paint.Right, paint.Paint.Bottom, _palette.Background);
        }
        finally
        {
            Native.EndPaint(_window, in paint);
        }
    }

    private static void Fill(nint dc, int left, int top, int right, int bottom, uint color)
    {
        if (right <= left || bottom <= top) return;
        var brush = Native.CreateSolidBrush(color);
        if (brush == 0) return;
        try
        {
            var rectangle = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
            _ = Native.FillRect(dc, in rectangle, brush);
        }
        finally
        {
            Native.DeleteObject(brush);
        }
    }

    private unsafe void InvalidateLoadingBar()
    {
        var bar = LoadingBar();
        var rectangle = new Rect { Left = bar.Left, Top = bar.Top, Right = bar.Right, Bottom = bar.Bottom };
        Native.InvalidateRect(_window, &rectangle, false);
    }

    // The message procedure's part of the surface; true when it handled the message.
    private unsafe bool HandleLoadingMessage(uint message, nuint wParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WmTimer when wParam == GraceTimer:
                ShowLoadingSurface();
                return true;
            case WmTimer when wParam == AnimationTimer:
                if (_loading) InvalidateLoadingBar();
                else Native.KillTimer(_window, AnimationTimer);
                return true;
            case WmEraseBackground when _loading:
                result = 1;
                return true;
            case WmPaint when _loading:
                PaintLoadingSurface();
                return true;
            case WmSize when _loading:
                Native.InvalidateRect(_window, null, false);
                return true;
            case WmClose when _shown && !IsOpen:
                // Nothing to confirm before the page has loaded, and the application cannot close a window it has not
                // received yet: the window closes, and the caller's open returns it closed.
                _closedWhileLoading = true;
                Native.DestroyWindow(_window);
                return true;
            case WmAppAbandon:
                if (!IsOpen) Native.DestroyWindow(_window);
                return true;
            default:
                return false;
        }
    }

    // A caller that cancels or disposes a window that is still opening: it closes now, rather than once WebView2 is ready.
    private void AbandonOpening()
    {
        if (!IsOpen && Volatile.Read(ref _window) is not 0 and var window)
        {
            Native.PostMessage(window, WmAppAbandon, 0, 0);
        }
    }

    // After the window closed while WebView2 was starting: the thread keeps dispatching until the creation completes,
    // so the environment and controller are released (and their browser processes end) then, not with the process. It
    // gives up after COM's two-minute server start limit (#307), when WebView2 has stopped answering.
    private void SettleInitialization()
    {
        if (_initialization is not { IsCompleted: false }) return;
        var limit = Native.SetTimer(0, 0, 120_000, 0);
        try
        {
            while (_initialization is { IsCompleted: false } && Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Window == 0 && message.Value == WmTimer && message.WParam == limit) break;
                Dispatch(in message);
            }
        }
        finally
        {
            if (limit != 0) Native.KillTimer(0, limit);
        }
    }

    // A thread message (no window) is the synchronization context's wake-up after the window closed.
    private void Dispatch(in Message message)
    {
        if (message.Window == 0 && message.Value == WmAppDispatch)
        {
            DrainDispatchQueue();
            return;
        }
        Native.TranslateMessage(in message);
        Native.DispatchMessage(in message);
    }

    // HKCU\…\Themes\Personalize\AppsUseLightTheme, which Windows sets from Settings > Personalization > Colors.
    private static bool SystemUsesDarkTheme()
    {
        const uint RegistryDword = 0x00000010; // RRF_RT_REG_DWORD
        var currentUser = new nint(unchecked((int)0x80000001));
        var size = (uint)sizeof(uint);
        return Native.RegGetValue(currentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme",
                RegistryDword, 0, out var value, ref size) == 0
            && value == 0;
    }

    // Settings > Accessibility > Visual effects > Animation effects; off keeps the bar's segment still.
    private static bool ClientAreaAnimations()
    {
        const uint GetClientAreaAnimation = 0x1042; // SPI_GETCLIENTAREAANIMATION
        var enabled = 1;
        return !Native.SystemParametersInfo(GetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        internal nint DeviceContext;
        internal int Erase;
        internal Rect Paint;
        internal int Restore;
        internal int IncrementalUpdate;
        internal fixed byte Reserved[32];
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        internal static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool KillTimer(nint window, nuint id);

        [LibraryImport("user32.dll")]
        internal static partial nint BeginPaint(nint window, out PaintStruct paint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EndPaint(nint window, in PaintStruct paint);

        [LibraryImport("user32.dll")]
        internal static partial int FillRect(nint deviceContext, in Rect rectangle, nint brush);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool InvalidateRect(nint window, Rect* rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [LibraryImport("user32.dll")]
        internal static partial uint GetSysColor(int index);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SystemParametersInfo(uint action, uint parameter, ref int value, uint update);

        [LibraryImport("gdi32.dll")]
        internal static partial nint CreateSolidBrush(uint color);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(nint handle);

        [LibraryImport("gdi32.dll")]
        internal static partial int ExcludeClipRect(nint deviceContext, int left, int top, int right, int bottom);

        [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int RegGetValue(nint key, string subKey, string value, uint flags, nint type, out uint data, ref uint size);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();
    }
}
