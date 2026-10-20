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
// - GTK's own direction, which mirrors the window's header bar and GTK dialogs, is right to left for the Arabic
//   locale. GTK 4 derives it from the language's script, so it needs no translation: on the Ubuntu runner, whose
//   GTK 4 has no translation of "default:LTR", it is right to left as on NixOS;
// - the application can set GTK's direction itself from its own thread, as the guide shows.
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
            await window.InvokeNativeAsync(_ =>
            {
                messages = Marshal.PtrToStringUTF8(SetLocale(LcMessages, 0)) ?? "";
                // GTK 3 takes its direction from this translation; reported to show GTK 4 does not need it.
                translated = GLib.Functions.Dgettext("gtk40", "default:LTR");
                locale = Gtk.Functions.GetLocaleDirection().ToString();
                widgets = Gtk.Widget.GetDefaultDirection().ToString();
            });
            if (messages != "ar_EG.UTF-8")
                throw new InvalidOperationException($"The message locale is {messages}, not ar_EG.UTF-8; generate the locale.");
            if (locale != "Rtl" || widgets != "Rtl")
                throw new InvalidOperationException($"GTK's locale direction is {locale} and its widget direction {widgets} for {messages} (default:LTR → {translated}).");

            // The guide's override, from the application's thread, for a UI language other than the process locale.
            await SetDirectionAsync(Gtk.TextDirection.Ltr);
            await window.InvokeNativeAsync(_ => widgets = Gtk.Widget.GetDefaultDirection().ToString());
            if (widgets != "Ltr")
                throw new InvalidOperationException($"Setting GTK's default direction to Ltr left it {widgets}.");
            await SetDirectionAsync(Gtk.TextDirection.Rtl);
            Console.WriteLine($"PASS GTK 4 RTL: page language {pageLanguage} lays out right to left; GTK's direction is Rtl for {messages} (default:LTR → {translated}); the application can override it.");
            return 0;
        });
    }

    // As the guide shows: GTK applies the direction on its own thread, for every window.
    private static Task SetDirectionAsync(Gtk.TextDirection direction)
    {
        var set = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            Gtk.Widget.SetDefaultDirection(direction);
            set.TrySetResult();
            return false;
        });
        return set.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The process's message locale as GTK set it with setlocale(LC_ALL, "").
    private const int LcMessages = 5;

    [DllImport("libc.so.6", EntryPoint = "setlocale")]
    private static extern nint SetLocale(int category, nint locale);
}
