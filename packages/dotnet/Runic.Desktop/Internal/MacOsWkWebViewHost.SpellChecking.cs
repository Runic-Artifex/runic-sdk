namespace Runic.Desktop.Internal;

// DesktopHostOptions.SpellCheckingLanguages on macOS. WKWebView checks spelling with the application's NSSpellChecker,
// which belongs to the process: one language selects it, and several or none let macOS identify each paragraph's
// language among the user's spelling languages, because NSSpellChecker cannot be limited to a set. Turning the checker
// off for one window (DesktopWindowOptions.SpellChecking) is spellcheck="false" in its documents.
internal sealed partial class MacOsWkWebViewHost
{
    // Whether Runic selected a language, which no languages then undoes.
    private static bool s_spellingLanguageSelected;

    private Action<DesktopDiagnostic>? _reportDiagnostic;

    public ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return InvokeAsync(() => ApplySpellCheckingLanguages(languages), cancellationToken);
    }

    // Main thread.
    private void ApplySpellCheckingLanguages(IReadOnlyList<string> languages)
    {
        // Without languages, and none selected before, macOS's own choice stands, so the shared checker is not created:
        // creating it starts the spelling service, and since 0.13's spell checking did that for every window, the
        // native smoke intermittently failed to exit after its last window closed.
        if (languages.Count == 0 && !s_spellingLanguageSelected)
        {
            SpellingLanguages.FirstReport(null);
            return;
        }
        var type = Api.GetClass("NSSpellChecker");
        var checker = type == 0 ? 0 : Api.SendNint(type, "sharedSpellChecker");
        if (checker == 0) return;
        if (languages.Count == 0)
        {
            if (s_spellingLanguageSelected) Api.SendVoidBool(checker, "setAutomaticallyIdentifiesLanguages:", true);
            s_spellingLanguageSelected = false;
            SpellingLanguages.FirstReport(null);
            return;
        }
        var resolution = SpellingLanguages.Resolve(languages, Strings(Api.SendNint(Api.GetClass("NSLocale"), "preferredLanguages")),
            Strings(Api.SendNint(checker, "availableLanguages")));
        if (resolution.Dictionaries.Length == 1)
        {
            Api.SendVoidBool(checker, "setAutomaticallyIdentifiesLanguages:", false);
            var language = Api.CreateString(resolution.Dictionaries[0]);
            try
            {
                s_spellingLanguageSelected = Api.SendBool(checker, "setLanguage:", language);
            }
            finally
            {
                Api.SendVoid(language, "release");
            }
        }
        else
        {
            Api.SendVoidBool(checker, "setAutomaticallyIdentifiesLanguages:", true);
            s_spellingLanguageSelected = false;
        }
        var missing = resolution.Missing.Length == 0 ? null : SpellingLanguages.MissingDictionaries(resolution.Missing,
            "macOS has no spelling dictionary for the language. Remove it, or check the spelling languages in System Settings › Keyboard › Text Input.");
        if (SpellingLanguages.FirstReport(missing)) _reportDiagnostic?.Invoke(missing!);
    }

    // Main thread. The strings of an NSArray of NSString.
    private static List<string> Strings(nint array)
    {
        List<string> values = [];
        if (array == 0) return values;
        var count = (long)Api.SendUlong(array, "count");
        for (long index = 0; index < count; index++)
        {
            if (Api.SendNintNint(array, "objectAtIndex:", (nint)index) is not 0 and var value) values.Add(Api.Text(value));
        }
        return values;
    }
}
