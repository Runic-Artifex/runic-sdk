using System.Reflection;
using System.Text;
using Runic.Platform;
using Runic.Translations;
using TUnit.Core;
using static Runic.Application.Views.Translations.Tests.CatalogTests;

namespace Runic.Application.Views.Translations.Tests;

// The key coverage of runic-views (sdk#881): every message has English and German text with the same arguments, and
// every message has a consumer, so a text Runic adds fails here until it has German, and a message Runic stops using
// fails until it is removed. The page's groups (field, bridge, shortcut) are checked against the page's keys by the
// tests of @runic-artifex/views-translations; the shared platform.*_key modifier names are used by both.
public sealed class KeyCoverageTests
{
    // The groups of the catalog and who shows them.
    private static readonly string[] Groups = ["validation", "field", "platform", "menu", "bridge", "shortcut"];

    // Messages whose German is the English word.
    private static readonly HashSet<string> SameInGerman = ["platform_alt_key", "shortcut_escape", "shortcut_tab"];

    // DesktopPlatformTexts member by message: RunicViewsTranslations.PlatformTexts sets each from its message.
    private static readonly Dictionary<string, string> PlatformMessages = new()
    {
        [nameof(DesktopPlatformTexts.OpenFileTitle)] = "platform_open_file_title",
        [nameof(DesktopPlatformTexts.OpenFilesTitle)] = "platform_open_files_title",
        [nameof(DesktopPlatformTexts.OpenDirectoryTitle)] = "platform_open_directory_title",
        [nameof(DesktopPlatformTexts.OpenDirectoriesTitle)] = "platform_open_directories_title",
        [nameof(DesktopPlatformTexts.SaveFileTitle)] = "platform_save_file_title",
        [nameof(DesktopPlatformTexts.UntitledFileName)] = "platform_untitled_file_name",
        [nameof(DesktopPlatformTexts.ChooseApplicationTitle)] = "platform_choose_application_title",
        [nameof(DesktopPlatformTexts.SecretCollectionLabel)] = "platform_secret_collection_label",
        [nameof(DesktopPlatformTexts.ControlKeyName)] = "platform_control_key",
        [nameof(DesktopPlatformTexts.ShiftKeyName)] = "platform_shift_key",
        [nameof(DesktopPlatformTexts.AltKeyName)] = "platform_alt_key",
        [nameof(DesktopPlatformTexts.AccessDeniedTitle)] = "platform_access_denied_title",
        [nameof(DesktopPlatformTexts.ContentInUseMessage)] = "platform_content_in_use",
        [nameof(DesktopPlatformTexts.LaunchLinkInvalidMessage)] = "platform_launch_link_invalid",
        [nameof(DesktopPlatformTexts.ResourceUnavailableTitle)] = "platform_resource_unavailable_title",
        [nameof(DesktopPlatformTexts.ResourceUnavailableMessage)] = "platform_resource_unavailable",
        [nameof(DesktopPlatformTexts.LoadingTitle)] = "platform_loading_title",
    };

    internal static IReadOnlyList<(string Name, TranslationKey Key)> Messages() =>
        typeof(RunicViewsTextKeys).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(TranslationKey))
            .Select(property => (TranslationKey)property.GetValue(null)!)
            .Select(key => (key.Name, key))
            .OrderBy(message => message.Item1, StringComparer.Ordinal)
            .ToList();

    [Test]
    public void EveryMessageHasEnglishAndGermanWithTheSameArguments()
    {
        var english = Snapshot("en");
        var german = Snapshot("de");
        var messages = Messages();
        Require(messages.Count > 100, $"Only {messages.Count} messages were found.");
        foreach (var (name, key) in messages)
        {
            Require(english.TryGetContentLocale(key, out var englishLocale) && englishLocale == "en", $"{name} has no English text.");
            Require(german.TryGetContentLocale(key, out var germanLocale) && germanLocale == "de", $"{name} has no German text: {germanLocale}.");
            Require(english.TryGetPlaceholders(key, out var placeholders), $"{name} has no inputs.");
            // Each argument gets a value that shows in the text, so a German text that drops or swaps an argument differs.
            var inputs = placeholders.ToArray();
            var arguments = inputs.Select(Sample).ToArray();
            var (en, de) = (english.Format(key, arguments), german.Format(key, arguments));
            for (var index = 0; index < inputs.Length; index++)
            {
                var marker = Marker(inputs[index], index);
                Require(en.Contains(marker, StringComparison.Ordinal) == de.Contains(marker, StringComparison.Ordinal),
                    $"{name} uses its arguments differently: '{en}' and '{de}'.");
            }
            Require(de != en || SameInGerman.Contains(name), $"{name} is not translated: '{de}'.");
        }
    }

    [Test]
    public void EveryMessageHasAConsumer()
    {
        var keys = Messages().ToDictionary(message => message.Name, message => message.Key, StringComparer.Ordinal);
        var names = keys.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            Require(Groups.Any(group => name.StartsWith(group + "_", StringComparison.Ordinal)), $"{name} is in no group Runic shows.");

        // platform: one message per text of DesktopPlatformTexts, and PlatformTexts sets each from its message.
        var members = typeof(DesktopPlatformTexts).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string)).ToList();
        Require(members.Select(property => property.Name).Order().SequenceEqual(PlatformMessages.Keys.Order()),
            $"DesktopPlatformTexts: {string.Join(", ", members.Select(property => property.Name))}");
        Require(PlatformMessages.Values.Order().SequenceEqual(names.Where(name => name.StartsWith("platform_", StringComparison.Ordinal)).Order()),
            "A platform message is not a DesktopPlatformTexts text.");
        var german = Snapshot("de");
        var texts = RunicViewsTranslations.PlatformTexts(Context("de"));
        foreach (var member in members)
        {
            var name = PlatformMessages[member.Name];
            Require((string?)member.GetValue(texts) == german.Format(keys[name], []),
                $"DesktopPlatformTexts.{member.Name} does not come from {name}: '{member.GetValue(texts)}', '{german.Format(keys[name], [])}'.");
        }

        // menu: one message per RunicViewsMenuTitles title (CatalogTests checks those against DesktopStandardMenuTitles).
        var titles = typeof(RunicViewsMenuTitles).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => "menu_" + SnakeCase(property.Name)).Order();
        Require(titles.SequenceEqual(names.Where(name => name.StartsWith("menu_", StringComparison.Ordinal)).Order()),
            $"Menu titles: {string.Join(", ", titles)}");

        // validation: the messages ValidationText gives the built-in attributes (ValidationTextTests checks each).
        var validation = ValidationTextTests.Attributes().Concat(ValidationTextTests.Ranges()).Select(item => item().Key).ToHashSet();
        Require(validation.Order().SequenceEqual(names.Where(name => name.StartsWith("validation_", StringComparison.Ordinal)).Order()),
            $"Validation messages no attribute takes: {string.Join(", ", names.Where(name => name.StartsWith("validation_", StringComparison.Ordinal)).Except(validation))}");
    }

    private static ITranslationSnapshot Snapshot(string locale) =>
        Context(locale).GetManager(RunicViewsTranslations.CatalogId).Current;

    // What an argument's sample value shows in a text: numbers below 1,000 show the same in every locale.
    private static string Marker(TranslationPlaceholderDescriptor placeholder, int index) =>
        placeholder.Type is TextArgumentType.Int or TextArgumentType.Number ? (871 + index).ToString(System.Globalization.CultureInfo.InvariantCulture) : $"‹{index}›";

    private static TextArgument Sample(TranslationPlaceholderDescriptor placeholder, int index) => placeholder.Type switch
    {
        TextArgumentType.Int => new TextArgument(placeholder.Name, 871L + index, placeholder.Format),
        TextArgumentType.Number => new TextArgument(placeholder.Name, 871m + index, placeholder.Format),
        TextArgumentType.List => new TextArgument(placeholder.Name, [Marker(placeholder, index), "‹x›"]),
        _ => new TextArgument(placeholder.Name, Marker(placeholder, index)),
    };

    private static string SnakeCase(string name)
    {
        var text = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsUpper(character) && text.Length > 0) text.Append('_');
            text.Append(char.ToLowerInvariant(character));
        }
        return text.ToString();
    }
}
