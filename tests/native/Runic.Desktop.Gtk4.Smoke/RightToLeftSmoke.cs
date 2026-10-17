using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Runic.Desktop;
using Runic.Desktop.Gtk4;

// --rtl (W315-019): run with an Arabic process locale, for example
// `LC_ALL=ar_EG.UTF-8 LANGUAGE=ar dbus-run-session -- xvfb-run -a`. Checks what the right-to-left guide tells an
// application to rely on with GTK 4 and WebKitGTK 6:
// - the page reads the process locale in navigator.language, so it can choose its own lang and dir;
// - WebKitGTK has Intl.Locale.prototype.maximize, which Runic Translations' localeDirection uses;
// - a page with dir="rtl" lays out right to left;
// - GTK's own direction, which mirrors the window's header bar and GTK dialogs, is right to left exactly when GTK's
//   translation for the locale says so. Ubuntu 24.04's GTK 4 language packs have no such translation for Arabic,
//   Hebrew or Persian, while NixOS's GTK 4 has one, so both outcomes are accepted and reported;
// - the application can set GTK's direction itself from any thread, as the guide shows.
[SupportedOSPlatform("linux")]
internal static class RightToLeftSmoke
{
    private const string Page =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>RTL</title>
        <style>#row{display:flex} #row span{width:100px}</style></head>
        <body><div id="row"><span id="first">أول</span><span id="second">ثاني</span></div>
        <script>
        // The page's own choice, as with Runic Translations' localeDirection: the likely script of the language.
        const rtl = new Set(["Adlm", "Arab", "Hebr", "Nkoo", "Rohg", "Syrc", "Thaa"]);
        const language = navigator.language;
        document.documentElement.lang = language;
        document.documentElement.dir = rtl.has(new Intl.Locale(language).maximize().script) ? "rtl" : "ltr";
        </script></body></html>
        """;

    private const string Probe =
        """
        const first = document.getElementById("first").getBoundingClientRect();
        const second = document.getElementById("second").getBoundingClientRect();
        return JSON.stringify({
          language: navigator.language,
          dir: document.documentElement.dir,
          direction: getComputedStyle(document.body).direction,
          firstIsRight: first.left > second.left,
        });
        """;

    internal static int Run()
    {
        var language = Environment.GetEnvironmentVariable("LANGUAGE") ?? Environment.GetEnvironmentVariable("LANG") ?? "";
        if (!language.StartsWith("ar", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --rtl with an Arabic locale, for example LC_ALL=ar_EG.UTF-8 LANGUAGE=ar.");
        var options = new DesktopHostOptions().WithGtk4() with { WaitForConnection = true };
        return DesktopEventLoop.Run(options, async host =>
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 240 });
            using var page = JsonDocument.Parse(await surface.ExecuteJavaScriptAsync(Probe, TimeSpan.FromSeconds(10)));
            var root = page.RootElement;
            var pageLanguage = root.GetProperty("language").GetString() ?? "";
            if (!pageLanguage.StartsWith("ar", StringComparison.Ordinal) || root.GetProperty("dir").GetString() != "rtl"
                || root.GetProperty("direction").GetString() != "rtl" || !root.GetProperty("firstIsRight").GetBoolean())
                throw new InvalidOperationException($"The WebKitGTK 6 page did not follow the Arabic locale: {root.GetRawText()}.");

            string translated = "", locale = "", widgets = "", messages = "";
            await window.DispatchNativeAsync(_ =>
            {
                messages = Marshal.PtrToStringUTF8(SetLocale(LcMessages, 0)) ?? "";
                // GTK takes its direction from its own translation of "default:LTR" for the process locale.
                translated = GLib.Functions.Dgettext("gtk40", "default:LTR");
                locale = Gtk.Functions.GetLocaleDirection().ToString();
                widgets = Gtk.Widget.GetDefaultDirection().ToString();
            });
            if (messages != "ar_EG.UTF-8")
                throw new InvalidOperationException($"The message locale is {messages}, not ar_EG.UTF-8; generate the locale.");
            var expected = translated == "default:RTL" ? "Rtl" : "Ltr";
            if (locale != expected || widgets != expected)
                throw new InvalidOperationException($"GTK translates default:LTR as {translated}, but its locale direction is {locale} and its widget direction {widgets}.");
            var gtk = expected == "Rtl"
                ? "GTK's direction follows the locale (Rtl)"
                : $"GTK has no right-to-left translation for {messages} (default:LTR → {translated}), so its direction stays Ltr";

            // The guide's override, from the application's thread: GTK's direction for every window.
            var set = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
            {
                Gtk.Widget.SetDefaultDirection(Gtk.TextDirection.Rtl);
                set.TrySetResult();
                return false;
            });
            await set.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await window.DispatchNativeAsync(_ => widgets = Gtk.Widget.GetDefaultDirection().ToString());
            if (widgets != "Rtl")
                throw new InvalidOperationException($"Setting GTK's default direction left it {widgets}.");
            Console.WriteLine($"PASS GTK 4 RTL: page language {pageLanguage} lays out right to left; {gtk}; the application can set it to Rtl.");
            return 0;
        });
    }

    // The process's message locale as GTK set it with setlocale(LC_ALL, "").
    private const int LcMessages = 5;

    [DllImport("libc.so.6", EntryPoint = "setlocale")]
    private static extern nint SetLocale(int category, nint locale);
}
