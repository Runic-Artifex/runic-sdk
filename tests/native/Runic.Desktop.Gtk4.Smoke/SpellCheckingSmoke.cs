using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;

// --spelling gtk3|gtk4 (#662) under Xvfb: the window turns WebKitGTK's spell checker on with enchant dictionaries for
// DesktopHostOptions.SpellCheckingLanguages, DesktopHost.SetSpellCheckingLanguagesAsync changes them in the open window,
// and a window with SpellChecking = false starts its documents with spellcheck="false" on the root. The dictionaries are
// those installed (an en_US hunspell dictionary in CI); a language without one is reported as missing instead.
[SupportedOSPlatform("linux")]
internal static class SpellCheckingSmoke
{
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic spelling</title></head>
        <body><textarea>Thiss is wrongg</textarea></body></html>
        """;

    private static string s_webKit = "";

    internal static int Run(string toolkit)
    {
        List<DesktopDiagnostic> diagnostics = [];
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        s_webKit = toolkit == "gtk3" ? "libwebkit2gtk-4.1.so.0" : "libwebkitgtk-6.0.so.4";
        options = options with
        {
            WaitForConnection = true,
            SpellCheckingLanguages = ["en", "xx-XX"],
            DiagnosticSink = diagnostic => { lock (diagnostics) diagnostics.Add(diagnostic); },
        };
        return DesktopEventLoop.Run(options, host => RunAsync(host, toolkit, diagnostics));
    }

    private static async Task<int> RunAsync(DesktopHost host, string toolkit, List<DesktopDiagnostic> diagnostics)
    {
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 240 });
            var (enabled, loaded) = await ReadCheckerAsync(window);
            Require(enabled, "the spell checker on");
            Console.WriteLine($"Spelling ({toolkit}): on with [{string.Join(", ", loaded)}] for en.");
            Require(loaded.All(static name => name.StartsWith("en", StringComparison.Ordinal)), "only English dictionaries");
            var missing = WaitForMissing(diagnostics);
            // en is missing only on a machine without an English dictionary.
            Require(missing.Message.Contains(loaded.Length > 0 ? "for xx-XX;" : "for en, xx-XX;", StringComparison.Ordinal),
                $"the languages without a dictionary reported, got: {missing.Message}");
            var root = await surface.ExecuteJavaScriptAsync("return String(document.documentElement.getAttribute('spellcheck'));");
            Require(root == "null", $"no spellcheck attribute on the root by default, got {root}");

            // The open window follows the application's new languages.
            await host.SetSpellCheckingLanguagesAsync(["en-US"]);
            (_, loaded) = await ReadCheckerAsync(window);
            Require(loaded is [] or ["en_US"], $"en_US alone after the change, got [{string.Join(", ", loaded)}]");
            Console.WriteLine($"Spelling ({toolkit}): [{string.Join(", ", loaded)}] after SetSpellCheckingLanguagesAsync(en-US).");
            await window.CloseAsync();
        }

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
        {
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 480, Height = 240, SpellChecking = false,
            });
            var root = await surface.ExecuteJavaScriptAsync(
                "return document.documentElement.getAttribute('spellcheck') + ' ' + document.querySelector('textarea').spellcheck;");
            Require(root == "false false", $"spellcheck=\"false\" on the root, inherited by the text area, got {root}");
            await window.CloseAsync();
        }
        Console.WriteLine($"PASS {toolkit} spell checking: on with the application's languages, changed in an open window, off in a window.");
        return 0;
    }

    private static DesktopDiagnostic WaitForMissing(List<DesktopDiagnostic> diagnostics)
    {
        lock (diagnostics)
        {
            return diagnostics.FirstOrDefault(static item => item.Code == "spell-checking-dictionary-missing")
                ?? throw new InvalidOperationException("Spelling: expected spell-checking-dictionary-missing for xx-XX.");
        }
    }

    // The process's checker, which every WebKitWebContext reports, read through a context of its own on the GTK thread.
    private static async Task<(bool Enabled, string[] Loaded)> ReadCheckerAsync(DesktopWindow window)
    {
        var result = (false, Array.Empty<string>());
        await window.InvokeNativeAsync(_ =>
        {
            var library = NativeLibrary.Load(s_webKit);
            unsafe
            {
                var create = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(library, "webkit_web_context_new");
                var enabled = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "webkit_web_context_get_spell_checking_enabled");
                var languages = (delegate* unmanaged[Cdecl]<nint, nint*>)NativeLibrary.GetExport(library, "webkit_web_context_get_spell_checking_languages");
                var unref = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(NativeLibrary.Load("libgobject-2.0.so.0"), "g_object_unref");
                var context = create();
                try
                {
                    List<string> loaded = [];
                    for (var list = languages(context); list != null && *list != 0; list++)
                        loaded.Add(Marshal.PtrToStringUTF8(*list)!);
                    result = (enabled(context) != 0, [.. loaded]);
                }
                finally
                {
                    unref(context);
                }
            }
        });
        return result;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Spelling: expected " + what + ".");
    }
}
