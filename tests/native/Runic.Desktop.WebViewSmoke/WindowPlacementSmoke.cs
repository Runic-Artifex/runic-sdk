using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#897 W1a, --window-placement: DesktopWindow.MaximizeAsync, RestoreAsync and SetPlacementAsync in a real native window.
// Linked into Runic.Desktop.Gtk4.Smoke for GTK 4.
// - Maximize and restore are idempotent: a second call leaves the state as it is.
// - SetPlacementAsync round-trips through DesktopWindowState.Placement (X/Y exact, the size within one unit, as at open)
//   while the window is normal, maximized, minimized and hidden; a maximized, minimized or hidden window then restores
//   to the placement's bounds, and a placement's maximized state holds once the window is shown again.
// - RestoreAsync of a window minimized while maximized ends at its normal bounds (D064.13).
// - macOS: the frame setFrame:display: gives a miniaturized or hidden window is the frame it has when it is shown again
//   (window-api-0.19/DESIGN.md §3.1, a claim this smoke measures).
// - Windows: the activation of each step is printed, and RestoreAsync of an inactive maximized window leaves it inactive
//   (SW_SHOWNOACTIVATE, the design's second measured claim).
// Linux needs a window manager (--window-manager under Openbox) to maximize and minimize; without one only the normal
// placement runs. Wayland refuses a placement with a position as a whole (D064.11).
internal static class WindowPlacementSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic window placement</title>
        </head><body style="margin:0">placement</body></html>
        """;

    internal static int Run(DesktopHostOptions hostOptions, bool windowManager) =>
        DesktopEventLoop.Run(hostOptions, host => RunAsync(host, windowManager));


    private static async Task<int> RunAsync(DesktopHost host, bool windowManager)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 520, Height = 380, X = 200, Y = 140,
        });
        try
        {
            Require(window.Capabilities.HasFlag(DesktopWindowCapabilities.WindowPlacement),
                $"the WindowPlacement capability, not {window.Capabilities}");
            await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
            await WaitForAsync(window, static state => state.Bounds is not null, "its state");
            var opened = (await WaitForStableAsync(window)).Placement;
            var positioned = opened.X is not null;
            Console.WriteLine($"Window placement: opened with {opened}{Activation(window)}.");
            if (!positioned) await ExercisePositionRefusalAsync(window, opened);

            var normal = Shifted(opened, 40, 30, 60, 40);
            await window.SetPlacementAsync(normal);
            await ExpectAsync(window, normal, "a placement while normal");
            if (OperatingSystem.IsLinux() && !windowManager)
            {
                Console.WriteLine("Window placement: no window manager maximizes or minimizes the window; those steps are skipped.");
                Console.WriteLine("PASS window placement: a normal placement round-trips.");
                return 0;
            }

            await ExerciseMaximizeAndRestoreAsync(window, normal);
            await ExerciseWhileMaximizedAsync(window, normal);
            await ExerciseWhileMinimizedAsync(window, normal);
            await ExerciseWhileHiddenAsync(window, normal);
            if (OperatingSystem.IsWindows()) await ExerciseInactiveRestoreAsync(host, window);
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("PASS window placement: maximize, restore and placements while normal, maximized, minimized and hidden.");
        return 0;
    }

    // D064.11: on Wayland a placement with a position changes nothing; one without applies.
    private static async Task ExercisePositionRefusalAsync(DesktopWindow window, DesktopWindowPlacement opened)
    {
        var before = window.State;
        try
        {
            await window.SetPlacementAsync(opened with { X = 10, Y = 10, Maximized = true });
            throw new InvalidOperationException("Window placement: expected a placement with a position to fail without positions.");
        }
        catch (DesktopException error) when (error.Code == "window-position-unavailable")
        {
            await Task.Delay(Settle);
            Require(window.State is { IsMaximized: false } && window.State.Placement == before!.Placement,
                $"the refused placement to change nothing: {before} → {window.State}");
            Console.WriteLine($"Window placement: a position was refused as a whole: {error.Message}");
        }
    }

    // Idempotent: MaximizeAsync twice stays maximized, RestoreAsync twice stays normal, at the placement's bounds.
    private static async Task ExerciseMaximizeAndRestoreAsync(DesktopWindow window, DesktopWindowPlacement normal)
    {
        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        await window.MaximizeAsync();
        await Task.Delay(Settle);
        Require(window.State!.IsMaximized, $"a second MaximizeAsync to leave the window maximized: {window.State}");
        Require(Matches(window.State.Placement, normal with { Maximized = true }),
            $"the maximized window to restore to {normal}: {window.State.Placement}");
        Console.WriteLine($"Window placement: maximized twice, still maximized{Activation(window)}.");

        await window.RestoreAsync();
        await ExpectAsync(window, normal, "the restored window");
        await window.RestoreAsync();
        await Task.Delay(Settle);
        Require(window.State is { IsMaximized: false } state && Matches(state.Placement, normal),
            $"a second RestoreAsync to leave the window normal at {normal}: {window.State}");
        Console.WriteLine($"Window placement: restored twice, still normal at {window.State!.Placement}{Activation(window)}.");
    }

    // A maximized window takes new restored bounds and stays maximized; restoring goes there. A placement that is not
    // maximized restores the window to its bounds.
    private static async Task ExerciseWhileMaximizedAsync(DesktopWindow window, DesktopWindowPlacement normal)
    {
        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        var restored = Shifted(normal, -20, 20, -40, 30) with { Maximized = true };
        await window.SetPlacementAsync(restored);
        await ExpectAsync(window, restored, "a maximized placement while maximized");
        await window.RestoreAsync();
        await ExpectAsync(window, restored with { Maximized = false }, "the window restored to the bounds set while maximized");

        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        await window.SetPlacementAsync(normal);
        await ExpectAsync(window, normal, "a normal placement while maximized");
        Console.WriteLine($"Window placement: placements while maximized round-trip{Activation(window)}.");
    }

    private static async Task ExerciseWhileMinimizedAsync(DesktopWindow window, DesktopWindowPlacement normal)
    {
        // D064.13: minimized while maximized, RestoreAsync ends normal.
        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        await MinimizeAsync(window);
        await window.RestoreAsync();
        await WaitForAsync(window, static state => !state.IsMaximized, "the window restored normal from minimized-while-maximized",
            activity: static activity => !activity.IsMinimized);
        await ExpectAsync(window, normal, "the normal bounds after restoring from minimized-while-maximized");
        Console.WriteLine($"Window placement: minimized while maximized, RestoreAsync ended normal{Activation(window)}.");

        // A placement while minimized: reported at once, the window stays minimized and restores to it.
        await MinimizeAsync(window);
        var minimized = Shifted(normal, 30, -10, 20, 20);
        await window.SetPlacementAsync(minimized);
        await WaitForAsync(window, state => Matches(state.Placement, minimized), $"the placement {minimized} reported while minimized");
        await Task.Delay(Settle);
        Require(window.Activity is { IsMinimized: true }, $"the window to stay minimized: {window.Activity}");
        if (OperatingSystem.IsMacOS()) Console.WriteLine($"Window placement: the miniaturized window's frame is {await MacFrameAsync(window)}.");
        await window.RestoreAsync();
        await ExpectAsync(window, minimized, "the window restored to the placement set while minimized");

        // A maximized placement while minimized: restoring still ends normal, at its bounds.
        await MinimizeAsync(window);
        var maximized = Shifted(normal, 10, 10, 0, 0) with { Maximized = true };
        await window.SetPlacementAsync(maximized);
        await WaitForAsync(window, state => Matches(state.Placement, maximized), $"the placement {maximized} reported while minimized");
        await window.RestoreAsync();
        await ExpectAsync(window, maximized with { Maximized = false }, "the window restored normal to the maximized placement's bounds");
        Console.WriteLine($"Window placement: placements while minimized round-trip{Activation(window)}.");
    }

    private static async Task ExerciseWhileHiddenAsync(DesktopWindow window, DesktopWindowPlacement normal)
    {
        // Hidden, a maximized placement: hidden still, then shown maximized, restoring to its bounds.
        await window.HideAsync();
        await WaitForAsync(window, static _ => true, "the hidden window", activity: static activity => !activity.IsVisible);
        var maximized = Shifted(normal, -30, 15, 30, -20) with { Maximized = true };
        await window.SetPlacementAsync(maximized);
        await WaitForAsync(window, state => Matches(state.Placement, maximized), $"the placement {maximized} reported while hidden");
        await Task.Delay(Settle);
        Require(window.Activity is { IsVisible: false }, $"the window to stay hidden: {window.Activity}");
        if (OperatingSystem.IsMacOS()) Console.WriteLine($"Window placement: the hidden window's frame is {await MacFrameAsync(window)}.");
        await window.ShowAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the hidden window shown maximized",
            activity: static activity => activity.IsVisible);
        await window.RestoreAsync();
        await ExpectAsync(window, maximized with { Maximized = false }, "the shown window restored to the placement set while hidden");

        // Hidden while maximized, a normal placement: shown normal at its bounds.
        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        await window.HideAsync();
        await WaitForAsync(window, static _ => true, "the hidden window", activity: static activity => !activity.IsVisible);
        await window.SetPlacementAsync(normal);
        await WaitForAsync(window, state => Matches(state.Placement, normal), $"the placement {normal} reported while hidden");
        await window.ShowAsync();
        await ExpectAsync(window, normal, "the hidden maximized window shown normal at the placement");

        // Hidden, MaximizeAsync: hidden still, shown maximized.
        await window.HideAsync();
        await WaitForAsync(window, static _ => true, "the hidden window", activity: static activity => !activity.IsVisible);
        await window.MaximizeAsync();
        await Task.Delay(Settle);
        Require(window.Activity is { IsVisible: false }, $"MaximizeAsync to keep a hidden window hidden: {window.Activity}");
        Require(window.State!.Placement.Maximized, $"the hidden window to report that it shows maximized: {window.State.Placement}");
        await window.ShowAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the hidden window shown maximized",
            activity: static activity => activity.IsVisible);
        await window.RestoreAsync();
        await ExpectAsync(window, normal, "the window restored after showing maximized");
        Console.WriteLine($"Window placement: placements and MaximizeAsync while hidden hold until shown{Activation(window)}.");
    }

    // Windows: SW_SHOWNOACTIVATE restores an inactive maximized window without activating it. Another window of the
    // smoke takes the activation first; where Windows refuses this process the foreground, the check is reported skipped.
    private static async Task ExerciseInactiveRestoreAsync(DesktopHost host, DesktopWindow window)
    {
        await window.MaximizeAsync();
        await WaitForAsync(window, static state => state.IsMaximized, "the maximized window");
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var other = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 300, Height = 240, X = 60, Y = 60,
        });
        try
        {
            await other.FocusAsync();
            if (await TryWaitForAsync(() => other.Activity is { IsActive: true } && window.Activity is { IsActive: false }, Deadline) is false)
            {
                Console.WriteLine($"Window placement: no-activate restore not measured: the other window did not take the activation " +
                    $"(this window{Activation(window)}, other{Activation(other)}).");
                await window.RestoreAsync();
                await WaitForAsync(window, static state => !state.IsMaximized, "the restored window");
                return;
            }
            await window.RestoreAsync();
            await WaitForAsync(window, static state => !state.IsMaximized, "the restored inactive window");
            await Task.Delay(Settle);
            Require(window.Activity is { IsActive: false } && other.Activity is { IsActive: true },
                $"RestoreAsync to leave an inactive window inactive (SW_SHOWNOACTIVATE): this window{Activation(window)}, other{Activation(other)}");
            Console.WriteLine("Window placement: RestoreAsync restored an inactive maximized window without activating it.");
        }
        finally { if (other.IsOpen) await other.CloseAsync(); }
    }

    private static async Task MinimizeAsync(DesktopWindow window)
    {
        await window.MinimizeAsync();
        await WaitForAsync(window, static _ => true, "the minimized window", activity: static activity => activity.IsMinimized);
        await Task.Delay(Settle);
    }

    private static async Task ExpectAsync(DesktopWindow window, DesktopWindowPlacement expected, string what)
    {
        var state = await WaitForAsync(window, state => state.IsMaximized == expected.Maximized && Matches(state.Placement, expected)
            && (expected.Maximized || state.X is null || state.X == expected.X && state.Y == expected.Y),
            $"{what}: {expected}", activity: static activity => activity.IsVisible);
        Console.WriteLine($"Window placement: {what}: {state.Placement}.");
    }

    // A placement with offsets from another: X/Y exactly, the size in the placement's units.
    private static DesktopWindowPlacement Shifted(DesktopWindowPlacement placement, int x, int y, int width, int height) => placement with
    {
        X = placement.X + x, Y = placement.Y + y, Width = placement.Width + width, Height = placement.Height + height,
        Maximized = false, FullScreen = false, Origin = DesktopCoordinateOrigin.TopLeft,
    };

    // As at open: the position exactly, the size within one unit (Windows converts device-independent pixels).
    private static bool Matches(DesktopWindowPlacement actual, DesktopWindowPlacement expected) =>
        actual.X == expected.X && actual.Y == expected.Y && Math.Abs(actual.Width - expected.Width) <= 1
        && Math.Abs(actual.Height - expected.Height) <= 1 && actual.Maximized == expected.Maximized && !actual.FullScreen;

    private static string Activation(DesktopWindow window) =>
        window.Activity is { } activity ? $" (active {activity.IsActive}, visible {activity.IsVisible}, minimized {activity.IsMinimized})" : "";

    private static async Task<DesktopWindowState> WaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected,
        Func<DesktopWindowActivity, bool>? activity = null)
    {
        var wait = TestTimeouts.Start(Deadline);
        DesktopWindowState? reached = null;
        if (await TryWaitForAsync(() => window.State is { } state && condition(state) && (activity is null || window.Activity is { } current && activity(current))
                && (reached = window.State) is not null, wait.Limit) is false)
        {
            throw new TimeoutException($"Window placement: the window did not report {expected}; last state: " +
                $"{window.State?.ToString() ?? "<none>"}{Activation(window)}.");
        }
        wait.Succeeded();
        return reached!;
    }

    // A window manager may still move a new window after it reported its first state, as Openbox does when it frames a
    // GTK 4 window on X11: the state is read once it has not changed for a second.
    private static async Task<DesktopWindowState> WaitForStableAsync(DesktopWindow window)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var state = window.State!;
        var since = elapsed.Elapsed;
        while (elapsed.Elapsed < Deadline && elapsed.Elapsed - since < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(50);
            if (window.State is { } current && current != state) (state, since) = (current, elapsed.Elapsed);
        }
        return state;
    }

    private static async Task<bool> TryWaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return false;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Window placement: expected " + what + ".");
    }

    // The NSWindow frame in AppKit's coordinates, read on the main thread.
    private static async Task<string> MacFrameAsync(DesktopWindow window)
    {
        CGRect frame = default;
        await window.InvokeNativeAsync(handle => frame = MacNative.Frame(handle));
        return $"({frame.X}, {frame.Y}, {frame.Width} x {frame.Height})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        internal double X;
        internal double Y;
        internal double Width;
        internal double Height;
    }

    private static class MacNative
    {
        // frame returns an NSRect, four doubles: in registers on arm64 (an HFA).
        internal static unsafe CGRect Frame(nint window)
        {
            var objc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
            var register = (delegate* unmanaged[Cdecl]<byte*, nint>)NativeLibrary.GetExport(objc, "sel_registerName");
            var send = (delegate* unmanaged[Cdecl]<nint, nint, CGRect>)NativeLibrary.GetExport(objc, "objc_msgSend");
            fixed (byte* name = "frame\0"u8) return send(window, register(name));
        }
    }
}
