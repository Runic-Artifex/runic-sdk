using System.Globalization;
using Runic.Application.Views;
using Runic.Translations;

namespace MultiWindow;

// The text this example's ViewModel produces, as Runic Translations text references: a message key, typed arguments
// and an English fallback. The page shows each in the Window's language with Frontend/src/translations.ts. An
// application generates both sides from one Runic Translations catalog (runic.json); the example keeps a two-language
// table instead, so it builds without the Translations toolchain.
public static class ProjectText
{
    public const string Catalog = "multi-window";
    public const string ContractFingerprint = "sha256:6d756c74692d77696e646f772d6578616d706c652d636174616c6f672d000001";

    public static TranslationReference Ready() => Reference("ready", "Ready.", []);

    public static TranslationReference Renamed(string title) =>
        Reference("renamed", $"Renamed to {title}.", new() { ["title"] = new(TextArgumentType.String, title) });

    public static TranslationReference TitleTooLong(int max) =>
        Reference("title_too_long", $"The title must be at most {max} characters.",
            new() { ["max"] = new(TextArgumentType.Int, max.ToString(CultureInfo.InvariantCulture)) });

    // The form Runic.Application.Views keeps in a validation message.
    public static RunicTextReference Text(TranslationReference reference) =>
        RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference);

    private static TranslationReference Reference(string key, string fallback, Dictionary<string, TranslationReferenceArgument> arguments) =>
        new(Catalog, ContractFingerprint, key, arguments, fallback);
}
