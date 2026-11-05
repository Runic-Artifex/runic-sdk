using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;

// S10 on Windows and macOS (see AppearanceSmoke.cs): the page's media queries and IDesktopSettings agree with the
// desktop, and on Windows follow a switch of it.
internal static partial class AppearanceSmoke
{
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task RunMacAsync(DesktopSurface surface, DesktopWindow window, bool expectIncreaseContrast)
    {
        bool increaseContrast = false, reduceMotion = false;
        await window.InvokeNativeAsync(_ =>
        {
            var workspace = Mac.Send(Mac.Class("NSWorkspace"), Mac.Sel("sharedWorkspace"));
            increaseContrast = Mac.SendBool(workspace, Mac.Sel("accessibilityDisplayShouldIncreaseContrast"));
            reduceMotion = Mac.SendBool(workspace, Mac.Sel("accessibilityDisplayShouldReduceMotion"));
        });
        if (expectIncreaseContrast && !increaseContrast)
            throw new InvalidOperationException("Increase contrast was written before the process started, but NSWorkspace reports it off.");
        // IDesktopSettings reads the same NSWorkspace values, and its watch starts with them.
        await using (var settings = Runic.Platform.MacOS.MacOSPlatformProvider.CreateSettings())
        {
            var expected = (increaseContrast ? DesktopContrast.More : DesktopContrast.NoPreference, reduceMotion);
            var read = await ReadSettingsAsync(settings);
            if ((read.Contrast, read.ReducedMotion) != expected || read.SystemColors is not null)
                throw new InvalidOperationException($"With Increase contrast {increaseContrast} and Reduce motion {reduceMotion} IDesktopSettings reads {read}.");
            using var cancel = new CancellationTokenSource(StepTimeout);
            await using var watch = settings.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
            if (!await watch.MoveNextAsync() || watch.Current is not PlatformResult<DesktopAppearance>.Success success || success.Value != read)
                throw new InvalidOperationException($"The watch started with {watch.Current}, not {read}.");
            Console.WriteLine($"PASS appearance: IDesktopSettings agrees with Increase contrast {increaseContrast} and Reduce motion {reduceMotion}: {read}");
        }
        var page = await ReadAsync(surface);
        var expectedContrast = increaseContrast ? "more" : "no-preference";
        var expectedMotion = reduceMotion ? "reduce" : "no-preference";
        if (page.Contrast != expectedContrast || page.ReducedMotion != expectedMotion || page.ForcedColors == "active")
            throw new InvalidOperationException(
                $"With Increase contrast {increaseContrast} and Reduce motion {reduceMotion} the page reports {page}.");
        Console.WriteLine($"PASS appearance: WKWebView agrees with Increase contrast {increaseContrast} and Reduce motion {reduceMotion}: {page}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static async Task RunWindowsAsync(DesktopHost host, DesktopSurface surface, bool toggleDesktop)
    {
        var animations = Win.ClientAreaAnimation();
        var highContrast = Win.HighContrastOn();
        var page = await ReadAsync(surface);
        if (page.ReducedMotion != (animations ? "no-preference" : "reduce") || page.ForcedColors != (highContrast ? "active" : "none"))
            throw new InvalidOperationException($"With client area animations {animations} and a contrast theme {highContrast} the page reports {page}.");
        Console.WriteLine($"PASS appearance: WebView2 agrees with animations {animations} and a contrast theme {highContrast}: {page}");
        // A clock that never advances: the watch's polling fallback never reads, so it reports a switch only from its
        // change notifications (WindowsSettingsChanges).
        await using var settings = new Runic.Platform.Windows.WindowsDesktopSettings { TimeProvider = new FrozenTime() };
        CheckSettings(await ReadSettingsAsync(settings), animations, highContrast, "as opened");
        if (!toggleDesktop) return;
        Win.SaveForRestore();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var watch = settings.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        if (!await watch.MoveNextAsync()) throw new InvalidOperationException("The settings watch ended.");
        Console.WriteLine($"APPEARANCE IDesktopSettings.WatchAsync listens for change notifications: {settings.ListensForChanges}.");
        // Each switch is checked in the watch, in the open page, which reports whether it follows while it is open, and
        // in a window opened after it, which has a WebView2 browser process of its own and must read the new setting.
        try
        {
            Win.SetClientAreaAnimation(!animations);
            CheckSettings(await WatchForAsync(watch, cancel, settings, value => value.ReducedMotion == animations, $"animations {!animations}"), !animations, highContrast,
                $"animations {!animations}");
            var reduced = animations ? "reduce" : "no-preference";
            await ReportLiveAsync(surface, page => page.ReducedMotion == reduced, $"animations {!animations}");
            page = await ReadInNewWindowAsync(host);
            if (page.ReducedMotion != reduced)
                throw new InvalidOperationException($"With client area animations {!animations}, a new window reports {page}.");
            Console.WriteLine($"PASS appearance: WebView2, a new window with animations {!animations}: {page}");
            Win.SetClientAreaAnimation(animations);
            await WatchForAsync(watch, cancel, settings, value => value.ReducedMotion == !animations, $"animations {animations} again");

            Win.SetHighContrast(!highContrast);
            var contrast = highContrast ? DesktopContrast.NoPreference : DesktopContrast.ForcedColors;
            CheckSettings(await WatchForAsync(watch, cancel, settings, value => value.Contrast == contrast && (value.SystemColors is not null) == !highContrast,
                $"a contrast theme {!highContrast}"), animations, !highContrast, $"a contrast theme {!highContrast}");
            var forced = highContrast ? "none" : "active";
            await ReportLiveAsync(surface, page => page.ForcedColors == forced, $"a contrast theme {!highContrast}");
            page = await ReadInNewWindowAsync(host);
            if (page.ForcedColors != forced)
                throw new InvalidOperationException($"With a contrast theme {!highContrast}, a new window reports {page}.");
            Console.WriteLine($"PASS appearance: WebView2, a new window with a contrast theme {!highContrast}: {page}");
        }
        finally
        {
            Win.Restore();
        }
    }

    private static async Task<DesktopAppearance> ReadSettingsAsync(IDesktopSettings settings) =>
        await settings.ReadAsync() is PlatformResult<DesktopAppearance>.Success { Value: var value } ? value
            : throw new InvalidOperationException("IDesktopSettings could not read the appearance.");

    // Reads the watch until it reports what `expected` accepts; a watch that only polls never does, its clock is frozen.
    // On a timeout the watch is cancelled and its pending read awaited, so it is not disposed while it runs.
    private static async Task<DesktopAppearance> WatchForAsync(IAsyncEnumerator<PlatformResult<DesktopAppearance>> watch,
        CancellationTokenSource cancel, Runic.Platform.Windows.WindowsDesktopSettings settings, Func<DesktopAppearance, bool> expected, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        DesktopAppearance? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(10))
        {
            var next = watch.MoveNextAsync().AsTask();
            if (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10) - started.Elapsed)) != next)
            {
                var listening = settings.ListensForChanges;
                await cancel.CancelAsync();
                try { await next; } catch (OperationCanceledException) { }
                throw new InvalidOperationException(
                    $"With {what}, IDesktopSettings.WatchAsync did not report it within 10 s from change notifications (listening: {listening}): last {last}.");
            }
            if (!await next) throw new InvalidOperationException("The settings watch ended.");
            if (watch.Current is PlatformResult<DesktopAppearance>.Success { Value: var value })
            {
                last = value;
                if (expected(value))
                {
                    Console.WriteLine($"PASS appearance: IDesktopSettings.WatchAsync reported {what} after {started.ElapsedMilliseconds} ms.");
                    return value;
                }
            }
        }
        throw new InvalidOperationException($"With {what}, IDesktopSettings.WatchAsync did not report it within 10 s from change notifications: last {last}.");
    }

    // Contrast, its palette and ReducedMotion agree with SystemParametersInfo and GetSysColor.
    private static void CheckSettings(DesktopAppearance appearance, bool animations, bool highContrast, string what)
    {
        var contrast = highContrast ? DesktopContrast.ForcedColors : DesktopContrast.NoPreference;
        if (appearance.Contrast != contrast || appearance.HighContrast != highContrast || appearance.ReducedMotion != !animations)
            throw new InvalidOperationException($"With client area animations {animations} and a contrast theme {highContrast} ({what}), IDesktopSettings reads {appearance}.");
        if (highContrast)
        {
            var colors = appearance.SystemColors ?? throw new InvalidOperationException($"A contrast theme is on ({what}), but IDesktopSettings reads no palette.");
            if (colors.Canvas != Win.SystemColor(Win.ColorWindow) || colors.CanvasText != Win.SystemColor(Win.ColorWindowText)
                || colors.Highlight != Win.SystemColor(Win.ColorHighlight) || colors.ButtonText != Win.SystemColor(Win.ColorButtonText))
                throw new InvalidOperationException($"The palette {colors} is not GetSysColor's ({what}).");
        }
        else if (appearance.SystemColors is not null) throw new InvalidOperationException($"Without a contrast theme ({what}) IDesktopSettings reads a palette.");
        Console.WriteLine($"PASS appearance: IDesktopSettings agrees with animations {animations} and a contrast theme {highContrast} ({what}): {appearance}");
    }

    // A TimeProvider whose timers never fire.
    private sealed class FrozenTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Never();
        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // --restore-desktop: a run killed by its step timeout restores nothing in its finally; CI's next step, which runs
    // always, restores what the run saved.
    internal static int RestoreDesktop()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        Console.WriteLine(Win.Restore() ? "APPEARANCE restored the saved Windows settings." : "APPEARANCE nothing to restore.");
        return 0;
    }

    // Whether the open page follows a change within five seconds; reported, not required.
    private static async Task ReportLiveAsync(DesktopSurface surface, Func<PageAppearance, bool> expected, string what)
    {
        PageAppearance? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            last = await ReadAsync(surface);
            if (expected(last)) break;
            await Task.Delay(100);
        }
        Console.WriteLine($"APPEARANCE WebView2, {what}: the open page {(expected(last!) ? "follows" : "does not follow")}: {last}");
    }

    private static async Task<PageAppearance> ReadInNewWindowAsync(DesktopHost host)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 });
        try
        {
            await surface.ExecuteJavaScriptAsync("return document.readyState;", StepTimeout);
            return await ReadAsync(surface);
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
    }

    private static partial class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Class(string name);
        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Sel(string name);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte SendByte(nint target, nint selector);
        internal static bool SendBool(nint target, nint selector) => SendByte(target, selector) != 0;
    }

    private static unsafe partial class Win
    {
        internal const int ColorWindow = 5, ColorWindowText = 8, ColorHighlight = 13, ColorButtonText = 18;
        // The settings before the switch, saved where the restore step of a killed run finds them.
        private static readonly string SavedPath = Path.Combine(Path.GetTempPath(), "runic-appearance-smoke-restore.json");

        internal static DesktopAccentColor SystemColor(int index)
        {
            var value = GetSysColor(index);
            return new((value & 0xFF) / 255d, ((value >> 8) & 0xFF) / 255d, ((value >> 16) & 0xFF) / 255d);
        }

        internal static void SaveForRestore()
        {
            var value = new HighContrast { Size = (uint)sizeof(HighContrast) };
            SystemParametersInfo(SpiGetHighContrast, value.Size, (nint)(&value), 0);
            // Three lines: animations, the HIGHCONTRAST flags, and the theme name (empty for none).
            File.WriteAllLines(SavedPath, [ClientAreaAnimation() ? "true" : "false",
                value.Flags.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.DefaultScheme == 0 ? "" : Marshal.PtrToStringUni(value.DefaultScheme) ?? ""]);
        }

        // Restores the saved animations, contrast theme on or off and the theme's name, then forgets them.
        internal static bool Restore()
        {
            if (!File.Exists(SavedPath)) return false;
            var saved = File.ReadAllLines(SavedPath);
            var animations = saved[0] == "true";
            if (ClientAreaAnimation() != animations) SetClientAreaAnimation(animations);
            var flags = uint.Parse(saved[1], System.Globalization.CultureInfo.InvariantCulture);
            if (HighContrastOn() != ((flags & HcfHighContrastOn) != 0)) SetHighContrast(flags, saved.Length > 2 && saved[2].Length > 0 ? saved[2] : null);
            File.Delete(SavedPath);
            return true;
        }
        private const uint SpiGetHighContrast = 0x0042, SpiSetHighContrast = 0x0043;
        private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;
        // SPIF_SENDCHANGE without SPIF_UPDATEINIFILE: the change lasts for the session and is broadcast.
        private const uint SendChange = 0x2;
        private const uint HcfHighContrastOn = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct HighContrast { public uint Size; public uint Flags; public nint DefaultScheme; }

        internal static bool ClientAreaAnimation()
        {
            int on = 1;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, (nint)(&on), 0) == 0 || on != 0;
        }

        internal static void SetClientAreaAnimation(bool on)
        {
            if (SystemParametersInfo(SpiSetClientAreaAnimation, 0, on ? 1 : 0, SendChange) == 0)
                throw new InvalidOperationException($"SPI_SETCLIENTAREAANIMATION failed with {Marshal.GetLastPInvokeError()}.");
            Broadcast(SpiSetClientAreaAnimation);
        }

        internal static bool HighContrastOn()
        {
            var value = new HighContrast { Size = (uint)sizeof(HighContrast) };
            return SystemParametersInfo(SpiGetHighContrast, value.Size, (nint)(&value), 0) != 0 && (value.Flags & HcfHighContrastOn) != 0;
        }

        // Keeps the current theme name, so turning it off and on again restores the same theme.
        internal static void SetHighContrast(bool on)
        {
            if (HighContrastOn() == on) return;
            var current = new HighContrast { Size = (uint)sizeof(HighContrast) };
            SystemParametersInfo(SpiGetHighContrast, current.Size, (nint)(&current), 0);
            SetHighContrast(on ? current.Flags | HcfHighContrastOn : current.Flags & ~HcfHighContrastOn,
                current.DefaultScheme == 0 ? null : Marshal.PtrToStringUni(current.DefaultScheme));
        }

        private static void SetHighContrast(uint flags, string? scheme)
        {
            var name = scheme is null ? 0 : Marshal.StringToHGlobalUni(scheme);
            try
            {
                var value = new HighContrast { Size = (uint)sizeof(HighContrast), Flags = flags, DefaultScheme = name };
                if (SystemParametersInfo(SpiSetHighContrast, value.Size, (nint)(&value), SendChange) == 0)
                    throw new InvalidOperationException($"SPI_SETHIGHCONTRAST failed with {Marshal.GetLastPInvokeError()}.");
                Broadcast(SpiSetHighContrast);
            }
            finally { if (name != 0) Marshal.FreeHGlobal(name); }
        }

        [LibraryImport("user32.dll")] private static partial uint GetSysColor(int index);

        // Without SPIF_UPDATEINIFILE, SPIF_SENDCHANGE may broadcast nothing; Settings saves the change and broadcasts
        // WM_SETTINGCHANGE with the action, which this repeats without changing the user's profile.
        private static void Broadcast(uint action) =>
            _ = SendMessageTimeout(0xFFFF, 0x001A, action, 0, 0x0002, 5000, out _);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static partial int SystemParametersInfo(uint action, uint parameter, nint value, uint flags);
    }
}
