using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// #662: DesktopWindowOptions.SpellChecking, DesktopHostOptions.SpellCheckingLanguages and
// DesktopHost.SetSpellCheckingLanguagesAsync, through a window host factory, and the mapping of BCP 47 tags to the
// dictionaries of enchant (WebKitGTK) and NSSpellChecker (macOS).
public sealed class SpellCheckingTests
{
    private static readonly string[] Hunspell = ["de_CH", "de_DE", "en_AU", "en_GB", "en_US", "sr_Latn_RS"];

    [Test]
    [Arguments("de-CH", "de_CH")]
    [Arguments("de-ch", "de_CH")]
    [Arguments("pt-BR", "pt_BR")]
    [Arguments("sr-Latn-RS", "sr_Latn_RS")]
    [Arguments("en-US-u-co-phonebk", "en_US")]
    [Arguments("es-419", "es_419")]
    [Arguments("en", "en")]
    [DisplayName("TagsBecomeDictionaryNames(tag: $tag)")]
    public void TagsBecomeDictionaryNames(string tag, string name) => Assert.Equal(name, SpellingLanguages.DictionaryName(tag));

    [Test]
    public void ARegionSelectsItsDictionaryOrTheLanguageButNeverAnotherRegion()
    {
        Assert.Equal(["de_CH", "en_GB"], SpellingLanguages.Resolve(["de-CH", "en-GB"], [], Hunspell).Dictionaries);
        var swiss = SpellingLanguages.Resolve(["de-CH"], [], ["de_DE", "en_US"]);
        Assert.Empty(swiss.Dictionaries);
        Assert.Equal(["de-CH"], swiss.Missing);
        // A dictionary for the language alone serves its regions, as BCP 47 lookup does (NSSpellChecker's de).
        Assert.Equal(["de"], SpellingLanguages.Resolve(["de-CH"], [], ["de", "en"]).Dictionaries);
        Assert.Equal(["sr_Latn_RS"], SpellingLanguages.Resolve(["sr-Latn-RS"], [], Hunspell).Dictionaries);
    }

    [Test]
    public void ALanguageWithoutRegionTakesTheUsersRegionThenTheCommonOneThenAny()
    {
        // The user's locale has the language: its region.
        Assert.Equal(["en_GB"], SpellingLanguages.Resolve(["en"], ["en_GB.UTF-8"], Hunspell).Dictionaries);
        Assert.Equal(["en_AU"], SpellingLanguages.Resolve(["en"], ["de_CH.UTF-8", "en-AU"], Hunspell).Dictionaries);
        // LANGUAGE lists several; the first with the language decides.
        Assert.Equal(["de_CH"], SpellingLanguages.Resolve(["de"], SpellingLanguages.LinuxUserLanguages(Environment(
            ("LANGUAGE", "fr_FR:de_CH:de"), ("LANG", "de_DE.UTF-8"))), Hunspell).Dictionaries);
        // Another language's locale: the language's most common region when the culture data knows it, else any region.
        var english = SpellingLanguages.Resolve(["en"], ["de_CH.UTF-8"], Hunspell).Dictionaries;
        Assert.Contains(Assert.Single(english), (string[])["en_US", "en_AU"]);
        Assert.Equal(["en_AU"], SpellingLanguages.Resolve(["en"], ["de_CH.UTF-8"], ["en_AU", "de_CH"]).Dictionaries);
        // A dictionary named for the language alone is taken as it is.
        Assert.Equal(["en"], SpellingLanguages.Resolve(["en"], ["en_GB.UTF-8"], ["en", "en_GB"]).Dictionaries);
        Assert.Equal(["fr"], SpellingLanguages.Resolve(["fr"], [], Hunspell).Missing);
    }

    [Test]
    public void WithoutAListOfDictionariesTheNamesToTryAreReturned()
    {
        var resolution = SpellingLanguages.Resolve(["de-CH", "en"], ["en_GB.UTF-8"], installed: null);
        Assert.Equal(["de_CH", "en_GB", "en"], resolution.Dictionaries);
        Assert.Empty(resolution.Missing);
    }

    [Test]
    public void TheUsersLanguagesComeFromTheLocaleVariablesWithoutCOrPosix()
    {
        var languages = SpellingLanguages.LinuxUserLanguages(Environment(
            ("LANGUAGE", "de_CH:en"), ("LC_ALL", ""), ("LC_MESSAGES", "C"), ("LANG", "fr_FR.UTF-8@euro")));
        Assert.Equal(["de_CH", "en", "C", "fr_FR.UTF-8@euro"], languages);
        Assert.Equal(["de-CH", "en", "fr-FR"], SpellingLanguages.UserTags(languages));
        Assert.Equal(["de_CH", "en_GB"], SpellingLanguages.Resolve(SpellingLanguages.UserTags(["POSIX", "de_CH.UTF-8", "en_GB"]),
            [], Hunspell).Dictionaries);
    }

    [Test]
    public void AMissingDictionaryIsReportedOnceUntilTheLanguagesChange()
    {
        var missing = SpellingLanguages.MissingDictionaries(["fr-CA"], "Install it.");
        Assert.Equal(SpellingLanguages.DictionaryMissingCode, missing.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, missing.Severity);
        Assert.Equal("DesktopHostOptions.SpellCheckingLanguages", missing.Option);
        SpellingLanguages.FirstReport(null);
        Assert.True(SpellingLanguages.FirstReport(missing));
        Assert.False(SpellingLanguages.FirstReport(missing));
        Assert.False(SpellingLanguages.FirstReport(null));
        Assert.True(SpellingLanguages.FirstReport(missing));
        SpellingLanguages.FirstReport(null);
    }

    [Test]
    public async Task SpellCheckingIsOnByDefaultAndTheHostGetsTheLanguages()
    {
        var factory = new SpellingFactory();
        await using var host = await DesktopHost.StartAsync(new()
        {
            WindowHostFactory = factory, WaitForConnection = false, SpellCheckingLanguages = ["de-CH", "en"],
        });
        Assert.Equal(["de-CH", "en"], host.SpellCheckingLanguages);
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts[0];
        Assert.True(native.Options!.SpellChecking);
        Assert.Equal(["de-CH", "en"], native.Options.SpellCheckingLanguages);
        Assert.DoesNotContain("spellcheck", native.Options.DocumentStartScript, StringComparison.Ordinal);

        await host.SetSpellCheckingLanguagesAsync(["fr-CA"]);
        Assert.Equal(["fr-CA"], host.SpellCheckingLanguages);
        Assert.Equal(["fr-CA"], Assert.Single(native.Languages));
        await host.SetSpellCheckingLanguagesAsync([]);
        Assert.Empty(native.Languages[^1]);
    }

    [Test]
    public async Task OffStartsEveryDocumentWithSpellcheckFalse()
    {
        var factory = new SpellingFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, SpellChecking = false });
        var options = factory.Hosts[0].Options!;
        Assert.False(options.SpellChecking);
        Assert.StartsWith(WebUiWindow.SpellCheckingOffScript, options.DocumentStartScript, StringComparison.Ordinal);
        // The credentials script still follows.
        Assert.Contains("runic-desktop.bootstrap", options.DocumentStartScript, StringComparison.Ordinal);
    }

    [Test]
    public async Task InvalidLanguagesAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DesktopHost.StartAsync(new() { SpellCheckingLanguages = ["en_US"] }));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await DesktopHost.StartAsync(new() { SpellCheckingLanguages = null! }));
        await using var host = await DesktopHost.StartAsync(new() { SpellCheckingLanguages = ["en"] });
        await Assert.ThrowsAsync<ArgumentException>(async () => await host.SetSpellCheckingLanguagesAsync(["de CH"]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await host.SetSpellCheckingLanguagesAsync([null!]));
        Assert.Equal(["en"], host.SpellCheckingLanguages);
    }

    [Test]
    public async Task ABrowserReportsOffIgnored()
    {
        await using var host = await DesktopHost.StartAsync(new());
        var preflight = host.GetPresentationPreflight(new() { Browser = BrowserKind.Chrome, SpellChecking = false });
        var diagnostic = Assert.Single(preflight.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.SpellChecking");
        Assert.Equal(DesktopDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.False(DesktopWindowOptionValidation.ToHostOptions(new() { SpellChecking = false }).SpellChecking);
        Assert.Equal("DesktopHostOptions.SpellCheckingLanguages", DesktopWindowOptionValidation.SpellCheckingLanguagesIgnored().Option);
    }

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    private sealed class SpellingFactory : IDesktopWindowHostFactory
    {
        internal List<SpellingHost> Hosts { get; } = [];
        public bool IsSupported => true;
        public IDesktopWindowHost Create()
        {
            var host = new SpellingHost();
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class SpellingHost : IDesktopSpellCheckingWindowHost
    {
        public event EventHandler? Closed;
        public bool SupportsDocumentStartScript => true;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;
        internal List<IReadOnlyList<string>> Languages { get; } = [];

        public ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default)
        {
            Languages.Add(languages);
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
