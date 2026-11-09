using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views.Generated;
using Runic.Platform;
using Runic.Translations;
using Runic.Translations.Hosting;
using TUnit.Core;
using static Runic.Application.Views.Translations.Tests.CatalogTests;

namespace Runic.Application.Views.Translations.Tests;

// services.AddRunicViewsTranslations(): the catalog joins the application's context, the window's validation errors take
// its text, and the platform texts follow the application's locale.
public sealed class ServiceCollectionTests
{
    [Test]
    public async Task AnApplicationShowsRunicsTextInGermanAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService();
        services.AddRunicTranslations(options => options.InitialLocale = "en");
        services.AddRunicViewsTranslations();
        services.AddScoped<SignUpViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, SignUpViewModel, IDisposable>>(
            _ => (transport, content, vm) => new SignUpBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider();
        var translations = provider.GetRequiredService<ITranslationContext>();
        var locale = provider.GetRequiredService<IRunicApplicationLocale>();
        using var following = locale.Follow(translations.SetLocaleAsync);
        try
        {
            await locale.SetLocaleAsync("de");
            await using var scope = provider.CreateAsyncScope();
            var model = scope.ServiceProvider.GetRequiredService<SignUpViewModel>();
            model.Nickname = "much too long";
            model.Age = 7;
            model.Email = "not an address";
            model.Validate();
            using var host = RunicWindowTestHost.Create<SignUpViewModel>(scope.ServiceProvider);

            // The first window started the platform texts in the application's locale.
            Require(DesktopPlatformTexts.Current.UntitledFileName == "Unbenannt", $"Platform texts: {DesktopPlatformTexts.Current}");

            var errors = ValidationErrors(host);
            Require(Text(translations, errors, "name") == "Name ist erforderlich.", $"[Required]: {errors}");
            Require(Text(translations, errors, "nickname") == "Nickname darf höchstens 8 Zeichen haben.", $"[MaxLength]: {errors}");
            Require(Text(translations, errors, "age") == "Age muss zwischen 18 und 120 liegen.", $"[Range]: {errors}");
            Require(!Error(errors, "email").TryGetProperty("text", out _), $"An attribute's own message took a text: {errors}");

            // A switch reaches the platform texts before LocaleChanged, and the references resolve in the new locale.
            string? untitled = null;
            locale.LocaleChanged += (_, _) => untitled = DesktopPlatformTexts.Current.UntitledFileName;
            await locale.SetLocaleAsync("en");
            Require(untitled == "Untitled", $"LocaleChanged saw {untitled}.");
            Require(Text(translations, errors, "name") == "Name is required.", "The reference did not follow the switch.");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    [Test]
    public async Task RegisteringTwiceRegistersOnceAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService();
        services.AddRunicViewsTranslations().AddRunicViewsTranslations();
        Require(services.Count(descriptor => descriptor.ServiceType == typeof(RunicValidationOptions)) == 1, "Two validation options.");
        Require(services.Count(descriptor => descriptor.ServiceType == typeof(IRunicWindowOpeningObserver)) == 1, "Two observers.");
        await using var provider = services.BuildServiceProvider();
        var text = provider.GetRequiredService<RunicViewsText>();
        Require(text.Messages.platform_untitled_file_name == "Untitled", "The catalog is not in the context.");
        Require(provider.GetRequiredService<RunicValidationOptions>().TextFor == RunicViewsTranslations.ValidationText, "TextFor was not set.");
    }

    [Test]
    public async Task TheApplicationsValidationOptionsWinAsync()
    {
        var own = new RunicValidationOptions { TextFor = (_, _) => null };
        var services = new ServiceCollection().AddSingleton(own).AddRunicViewsTranslations();
        await using var provider = services.BuildServiceProvider();
        Require(ReferenceEquals(provider.GetRequiredService<RunicValidationOptions>(), own), "The package replaced the application's options.");
    }

    private static JsonElement ValidationErrors(RunicWindowTestHost<SignUpViewModel> host)
        => host.Root.Snapshot().Json.GetProperty("validation").GetProperty("errors").Clone();

    private static JsonElement Error(JsonElement errors, string member) =>
        errors.EnumerateArray().First(error => error.GetProperty("path")[0].GetString() == member);

    private static string Text(ITranslationContext translations, JsonElement errors, string member) =>
        translations.Resolve(TranslationReference.Parse(Error(errors, member).GetProperty("text").GetRawText())).Text ?? "";
}
