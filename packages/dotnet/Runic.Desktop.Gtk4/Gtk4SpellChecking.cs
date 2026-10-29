using System.Runtime.Versioning;
using Runic.Desktop.Internal;
using WebKit;

namespace Runic.Desktop.Gtk4;

// Spell checking in the GTK 4 window (IDesktopSpellCheckingWindowHost): WebKitGTK checks nothing until its checker is on
// and has enchant dictionaries. Both belong to the process (WebKit's TextChecker), whichever window's context sets them,
// so every window applies DesktopHost.SpellCheckingLanguages, and a window that keeps the checker off
// (DesktopWindowOptions.SpellChecking) does so with spellcheck="false" in its documents.
[SupportedOSPlatform("linux")]
internal sealed partial class Gtk4WindowHost : IDesktopSpellCheckingWindowHost
{
    public ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (_webView?.GetContext() is { } context) ApplySpellChecking(context, languages);
        }, cancellationToken));
    }

    // GTK thread.
    private void ApplySpellChecking(WebContext context, IReadOnlyList<string> languages)
    {
        var (dictionaries, missing) = SpellingLanguages.ForWebKitGtk(languages);
        context.SetSpellCheckingEnabled(true);
        context.SetSpellCheckingLanguages(dictionaries);
        if (SpellingLanguages.FirstReport(missing)) _options?.DiagnosticSink?.Invoke(missing!);
    }
}
