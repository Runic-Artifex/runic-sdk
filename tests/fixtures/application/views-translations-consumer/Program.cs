using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Translations;
using Runic.Translations;
using Runic.Translations.Hosting;
using ViewsTranslationsConsumer;

// The application's catalog, its build-time overrides of runic-views, and Runic's own text, in one context.
var services = new ServiceCollection();
services.AddRunicTranslations(options => options.InitialLocale = "de")
    .AddCatalog(ConsumerTextCatalog.Registration, context => new ConsumerText(context))
    .AddOverlays(ConsumerTextCatalog.Overlays);
services.AddRunicViewsTranslations();
await using var provider = services.BuildServiceProvider();
var translations = provider.GetRequiredService<ITranslationContext>();
var required = RunicViewsTranslations.ValidationReference(new RequiredAttribute(),
    new RunicValidationMember(typeof(object), "Name", "Name", "The Name field is required.")) ?? throw new Exception("[Required] has no text.");

Check(provider.GetRequiredService<ConsumerText>().Messages.title, "Registrieren");
var german = RunicViewsTranslations.PlatformTexts(translations);
Check(german.UntitledFileName, "Neues Dokument");          // the application's German
Check(german.SaveFileTitle, "Datei speichern");            // the library's German
Check(translations.Resolve(required).Text, "Name ist erforderlich.");   // locale-first: German over the English override

await translations.SetLocaleAsync("en");
var english = RunicViewsTranslations.PlatformTexts(translations);
Check(english.UntitledFileName, "New document");
Check(english.SaveFileTitle, "Save file");
Check(translations.Resolve(required).Text, "Please fill in Name.");
Check(provider.GetRequiredService<RunicValidationOptions>().TextFor is not null ? "set" : "missing", "set");
Console.WriteLine("Runic.Application.Views.Translations consumer passed.");

static void Check(string? actual, string expected)
{
    if (actual != expected) throw new Exception($"Expected '{expected}', got '{actual}'.");
}
