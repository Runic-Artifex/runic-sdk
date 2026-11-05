using System.Runtime.InteropServices;
using System.Text.Json;
using Runic.Desktop;
using Runic.Platform;

// S10: the desktop's contrast and motion preferences reach the page's CSS media queries (prefers-contrast,
// forced-colors, prefers-reduced-motion) in each embedded engine, and follow a change while the window is open. Runic
// sets none of them; the smoke documents what each engine does (Runic.Desktop README, "Contrast, forced colors and
// reduced motion"). Run with --appearance.
//
// - WebKitGTK (Xvfb): GTK's gtk-enable-animations and gtk-theme-name are switched in the process, as the desktop's
//   settings daemon would: prefers-reduced-motion and prefers-contrast follow them. WebKitGTK 6.0 (GTK 4, linked into
//   Runic.Desktop.Gtk4.Smoke) reads GTK 4.20's gtk-interface-contrast and gtk-interface-reduced-motion only when its
//   first view starts, so they are set before the window opens.
// - WebView2: the page's values and IDesktopSettings' agree with the desktop's. With --toggle-desktop (CI only: it
//   changes the session's settings and restores them, also after a killed run with --restore-desktop), Windows' client
//   area animations and a contrast theme are switched with SystemParametersInfo: a watch started before the switch
//   reports it from change notifications alone (its polling clock is frozen), the contrast theme's palette is
//   GetSysColor's, and a window opened afterwards reports them; whether the open page follows is reported.
// - WKWebView: macOS settings cannot be switched without the user's consent, so the page's values and IDesktopSettings'
//   must agree with NSWorkspace's accessibilityDisplayShouldIncreaseContrast and accessibilityDisplayShouldReduceMotion.
//   CI runs it twice, the second time with Increase contrast written before the process starts (--expect-contrast), so
//   DesktopContrast.More and prefers-contrast: more are checked too.
internal static partial class AppearanceSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    // The value of each media feature that matches, or "unsupported" when none does (the engine does not evaluate it).
    private const string ReadScript = """
        const value = (feature, values) => values.find(v => matchMedia(`(${feature}: ${v})`).matches) ?? "unsupported";
        return JSON.stringify({
          contrast: value("prefers-contrast", ["more", "less", "custom", "no-preference"]),
          forcedColors: value("forced-colors", ["active", "none"]),
          reducedMotion: value("prefers-reduced-motion", ["reduce", "no-preference"]),
        });
        """;

    internal sealed record PageAppearance(string Contrast, string ForcedColors, string ReducedMotion)
    {
        public override string ToString() => $"prefers-contrast: {Contrast}, forced-colors: {ForcedColors}, prefers-reduced-motion: {ReducedMotion}";
    }

    // expectIncreaseContrast: CI wrote Increase contrast before this process started, so the macOS checks must see it.
    internal static int Run(DesktopHostOptions hostOptions, bool toggleDesktop, bool expectIncreaseContrast = false) =>
        DesktopEventLoop.Run(hostOptions, host => RunAsync(host, toggleDesktop, expectIncreaseContrast));

    private static async Task<int> RunAsync(DesktopHost host, bool toggleDesktop, bool expectIncreaseContrast)
    {
        // WebKitGTK 6.0 reads GTK 4's settings when its first view starts, and does not follow a later change (2.54).
        var gtk4Startup = OperatingSystem.IsLinux() && Gtk.Gtk4 && Gtk.HasProperty("gtk-interface-contrast") && Gtk.HasProperty("gtk-interface-reduced-motion");
        if (gtk4Startup)
        {
            Gtk.SetEnum("gtk-interface-contrast", Gtk.InterfaceContrastMore);
            Gtk.SetEnum("gtk-interface-reduced-motion", Gtk.ReducedMotionReduce);
        }
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 });
        try
        {
            await surface.ExecuteJavaScriptAsync("return document.readyState;", StepTimeout);
            var engine = OperatingSystem.IsWindows() ? "WebView2" : OperatingSystem.IsMacOS() ? "WKWebView" : "WebKitGTK";
            Console.WriteLine($"APPEARANCE {engine} as opened: {await ReadAsync(surface)}");
            if (OperatingSystem.IsLinux() && Gtk.Gtk4) await RunGtk4Async(surface, gtk4Startup);
            else if (OperatingSystem.IsLinux()) await RunGtkAsync(surface, window);
#if !RUNIC_GTK4_SMOKE // Runic.Desktop.Gtk4.Smoke builds without the Windows and macOS checks (AppearanceSmoke.Desktops.cs).
            else if (OperatingSystem.IsMacOS()) await RunMacAsync(surface, window, expectIncreaseContrast);
            else if (OperatingSystem.IsWindowsVersionAtLeast(8)) await RunWindowsAsync(host, surface, toggleDesktop);
#endif
        }
        finally { if (window.IsOpen) await window.CloseAsync(); }
        Console.WriteLine("APPEARANCE DONE");
        return 0;
    }

    private static async Task<PageAppearance> ReadAsync(DesktopSurface surface)
    {
        var json = await surface.ExecuteJavaScriptAsync(ReadScript, StepTimeout)
            ?? throw new InvalidOperationException("The page returned no media query values.");
        var values = JsonDocument.Parse(json).RootElement;
        return new(values.GetProperty("contrast").GetString()!, values.GetProperty("forcedColors").GetString()!,
            values.GetProperty("reducedMotion").GetString()!);
    }

    // Polls until the page reports what `expected` accepts: engines apply a setting change asynchronously.
    private static async Task<PageAppearance> ExpectAsync(DesktopSurface surface, Func<PageAppearance, bool> expected, string what)
    {
        PageAppearance? last = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            last = await ReadAsync(surface);
            if (expected(last)) return last;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"{what}: the page reports {last}.");
    }

    // GTK: the settings daemon (GNOME's xsettings, the portal's GtkSettings bridge) sets these GtkSettings properties
    // when the user changes the desktop's settings; WebKitGTK follows them.
    private static async Task RunGtkAsync(DesktopSurface surface, DesktopWindow window)
    {
        bool animations = true;
        string? theme = null;
        await window.InvokeNativeAsync(_ => { animations = Gtk.GetBoolean("gtk-enable-animations"); theme = Gtk.GetString("gtk-theme-name"); });
        try
        {
            await window.InvokeNativeAsync(_ => Gtk.SetBoolean("gtk-enable-animations", false));
            var page = await ExpectAsync(surface, page => page.ReducedMotion == "reduce", "With gtk-enable-animations off, prefers-reduced-motion is not reduce");
            Console.WriteLine($"PASS appearance: WebKitGTK, animations off: {page}");
            await window.InvokeNativeAsync(_ => Gtk.SetBoolean("gtk-enable-animations", true));
            page = await ExpectAsync(surface, page => page.ReducedMotion == "no-preference", "With gtk-enable-animations on, prefers-reduced-motion is not no-preference");
            Console.WriteLine($"PASS appearance: WebKitGTK, animations on: {page}");

            await window.InvokeNativeAsync(_ => Gtk.SetString("gtk-theme-name", "HighContrast"));
            page = await ExpectAsync(surface, page => page.Contrast == "more", "With the HighContrast GTK theme, prefers-contrast is not more");
            if (page.ForcedColors == "active") throw new InvalidOperationException($"WebKitGTK forced colours with the HighContrast theme: {page}.");
            Console.WriteLine($"PASS appearance: WebKitGTK, HighContrast theme: {page}");
            await window.InvokeNativeAsync(_ => Gtk.SetString("gtk-theme-name", "Adwaita"));
            page = await ExpectAsync(surface, page => page.Contrast == "no-preference", "With the Adwaita GTK theme, prefers-contrast is not no-preference");
            Console.WriteLine($"PASS appearance: WebKitGTK, Adwaita theme: {page}");
        }
        finally
        {
            await window.InvokeNativeAsync(_ =>
            {
                Gtk.SetBoolean("gtk-enable-animations", animations);
                if (theme is not null) Gtk.SetString("gtk-theme-name", theme);
            });
        }
    }

    // GTK 4.20 added gtk-interface-contrast and gtk-interface-reduced-motion, which WebKitGTK 6.0 reads when its first view
    // starts (set above); it does not follow gtk-enable-animations or the theme name, nor any change while it runs. An
    // older GTK 4, such as Ubuntu 24.04's, has neither setting.
    private static async Task RunGtk4Async(DesktopSurface surface, bool startup)
    {
        if (!startup)
        {
            Console.WriteLine("SKIP appearance: this GTK 4 has no gtk-interface-contrast (GTK 4.20 or later).");
            return;
        }
        var page = await ReadAsync(surface);
        if (page.Contrast != "more" || page.ReducedMotion != "reduce" || page.ForcedColors == "active")
            throw new InvalidOperationException($"With GTK 4's more contrast and reduced motion at start, the page reports {page}.");
        Console.WriteLine($"PASS appearance: WebKitGTK 6.0, more contrast and reduced motion at start: {page}");
    }

    private static unsafe partial class Gtk
    {
        private const nint GTypeBoolean = 5 << 2, GTypeString = 16 << 2;
        private const int GValueSize = 24;

        // GTK 4 when the process loaded it (Runic.Desktop.Gtk4), GTK 3 otherwise.
        private static nint Settings()
        {
            var library = DlOpen("libgtk-4.so.1", RtldNow | RtldNoLoad);
            if (library == 0) library = DlOpen("libgtk-3.so.0", RtldNow | RtldNoLoad);
            if (library == 0 || !NativeLibrary.TryGetExport(library, "gtk_settings_get_default", out var getDefault))
                throw new InvalidOperationException("The process has not loaded GTK.");
            return ((delegate* unmanaged[Cdecl]<nint>)getDefault)() is not 0 and var settings ? settings
                : throw new InvalidOperationException("GTK has no default settings.");
        }

        internal static bool Gtk4 => DlOpen("libgtk-4.so.1", RtldNow | RtldNoLoad) != 0;

        // g_object_class_find_property(G_OBJECT_GET_CLASS(settings), name).
        internal static bool HasProperty(string name) => FindProperty(*(nint*)Settings(), name) != 0;

        // GTK 4.20: GtkInterfaceContrast UNSUPPORTED, NO_PREFERENCE, MORE, LESS; GtkReducedMotion NO_PREFERENCE, REDUCE.
        internal const int InterfaceContrastMore = 2, ReducedMotionReduce = 1;

        internal static void SetEnum(string name, int number)
        {
            var settings = Settings();
            var pspec = FindProperty(*(nint*)settings, name);
            var value = stackalloc byte[GValueSize];
            new Span<byte>(value, GValueSize).Clear();
            InitValue((nint)value, *(nint*)(pspec + 3 * sizeof(nint)));
            try { SetValueEnum((nint)value, number); SetProperty(settings, name, (nint)value); }
            finally { UnsetValue((nint)value); }
        }

        private const int RtldNow = 2, RtldNoLoad = 4;
        [LibraryImport("libc", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)] private static partial nint DlOpen(string file, int mode);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_class_find_property", StringMarshalling = StringMarshalling.Utf8)] private static partial nint FindProperty(nint objectClass, string name);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_enum")] private static partial void SetValueEnum(nint value, int number);

        internal static bool GetBoolean(string name)
        {
            var value = stackalloc byte[GValueSize];
            new Span<byte>(value, GValueSize).Clear();
            InitValue((nint)value, GTypeBoolean);
            try { GetProperty(Settings(), name, (nint)value); return GetValueBoolean((nint)value) != 0; }
            finally { UnsetValue((nint)value); }
        }

        internal static void SetBoolean(string name, bool on)
        {
            var value = stackalloc byte[GValueSize];
            new Span<byte>(value, GValueSize).Clear();
            InitValue((nint)value, GTypeBoolean);
            try { SetValueBoolean((nint)value, on ? 1 : 0); SetProperty(Settings(), name, (nint)value); }
            finally { UnsetValue((nint)value); }
        }

        internal static string? GetString(string name)
        {
            var value = stackalloc byte[GValueSize];
            new Span<byte>(value, GValueSize).Clear();
            InitValue((nint)value, GTypeString);
            try { GetProperty(Settings(), name, (nint)value); return Marshal.PtrToStringUTF8(GetValueString((nint)value)); }
            finally { UnsetValue((nint)value); }
        }

        internal static void SetString(string name, string text)
        {
            var value = stackalloc byte[GValueSize];
            new Span<byte>(value, GValueSize).Clear();
            InitValue((nint)value, GTypeString);
            try { SetValueString((nint)value, text); SetProperty(Settings(), name, (nint)value); }
            finally { UnsetValue((nint)value); }
        }

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_init")] private static partial nint InitValue(nint value, nint type);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_unset")] private static partial void UnsetValue(nint value);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_boolean")] private static partial int GetValueBoolean(nint value);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_boolean")] private static partial void SetValueBoolean(nint value, int on);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_string")] private static partial nint GetValueString(nint value);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_string", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetValueString(nint value, string text);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_get_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void GetProperty(nint instance, string name, nint value);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_set_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetProperty(nint instance, string name, nint value);
    }

    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="color-scheme" content="light dark">
        <script src="webui.js"></script><title>Runic appearance smoke</title></head>
        <body><main><h1>Appearance</h1></main></body></html>
        """;
}
