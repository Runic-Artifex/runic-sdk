using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var translations = provider.GetRequiredService<ITranslationContext>();
        Require(RunicViewsTranslations.PlatformTexts(translations).UntitledFileName == "Untitled", "The catalog is not in the context.");
        Require(translations.Catalogs.Count(catalog => catalog.CatalogId == RunicViewsTranslations.CatalogId) == 1, "Two runic-views catalogs.");
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

    // A scoped context belongs to one window and cannot be resolved from the root provider, where window-opening
    // observers run (and with ValidateScopes, the Development default of Host.CreateApplicationBuilder, must not be).
    // The process-wide platform texts then follow an application-wide context that follows the application's locale.
    [Test]
    public async Task AScopedContextLeavesThePlatformTextsToTheApplicationsLocaleAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService();
        services.AddRunicViewsTranslations();
        // After the package registered: the application sets the lifetime.
        services.AddRunicTranslations(options => { options.InitialLocale = "en"; options.Lifetime = ServiceLifetime.Scoped; });
        AddSignUp(services);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var locale = provider.GetRequiredService<IRunicApplicationLocale>();
        try
        {
            await locale.SetLocaleAsync("de");
            await using var scope = provider.CreateAsyncScope();
            var windowContext = scope.ServiceProvider.GetRequiredService<ITranslationContext>();
            await windowContext.SetLocaleAsync("en");   // a window of its own language
            using var host = RunicWindowTestHost.Create<SignUpViewModel>(scope.ServiceProvider);
            Require(DesktopPlatformTexts.Current.UntitledFileName == "Unbenannt", $"Platform texts: {DesktopPlatformTexts.Current}");

            string? untitled = null;
            locale.LocaleChanged += (_, _) => untitled = DesktopPlatformTexts.Current.UntitledFileName;
            await locale.SetLocaleAsync("en");
            Require(untitled == "Untitled", $"LocaleChanged saw {untitled}.");
            await locale.SetLocaleAsync("de");
            Require(DesktopPlatformTexts.Current.SaveFileTitle == "Datei speichern", $"Platform texts: {DesktopPlatformTexts.Current}");
            Require(windowContext.RequestedLocale == "en", "The window's context followed the application-wide one.");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    // The cause of a smaller SupportedLocales, logged once when the first window opens: the application's catalog has
    // French, runic-views has not, so the locales every catalog has are en and de.
    [Test]
    public async Task LocalesTheCatalogLacksAreLoggedAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService();
        services.AddRunicTranslations(options => options.InitialLocale = "en").AddCatalog(TestTextCatalog.Registration);
        services.AddRunicViewsTranslations();
        AddSignUp(services);
        var logs = new RecordingLoggerFactory();
        await using var provider = services.BuildServiceProvider();
        try
        {
            var translations = provider.GetRequiredService<ITranslationContext>();
            Require(translations.SupportedLocales.SequenceEqual(["de", "en"]), $"Supported: {string.Join(", ", translations.SupportedLocales)}");
            await using var scope = provider.CreateAsyncScope();
            using var host = RunicWindowTestHost.Create<SignUpViewModel>(scope.ServiceProvider, new RunicWindowTestHostOptions { LoggerFactory = logs });
            using var second = RunicWindowTestHost.Create<SignUpViewModel>(scope.ServiceProvider, new RunicWindowTestHostOptions { LoggerFactory = logs });
            var events = logs.Events.Where(entry => entry.Id.Id == 1261).ToList();
            Require(events.Count == 1 && events[0].Id.Name == "LocalesWithoutRunicViewsText" && events[0].Message.Contains("have fr, which", StringComparison.Ordinal)
                && events[0].Category == "Runic.Application.Views.Translations", $"Events: {string.Join(" | ", logs.Events)}");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    [Test]
    public async Task NoLocalesAreLoggedWhenEveryCatalogHasRunicsLocalesAsync()
    {
        var services = new ServiceCollection().AddRunicWindowService();
        services.AddRunicViewsTranslations();
        AddSignUp(services);
        var logs = new RecordingLoggerFactory();
        await using var provider = services.BuildServiceProvider();
        try
        {
            await using var scope = provider.CreateAsyncScope();
            using var host = RunicWindowTestHost.Create<SignUpViewModel>(scope.ServiceProvider, new RunicWindowTestHostOptions { LoggerFactory = logs });
            Require(!logs.Events.Any(entry => entry.Id.Id == 1261), $"Events: {string.Join(" | ", logs.Events)}");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    private static void AddSignUp(IServiceCollection services)
    {
        services.AddScoped<SignUpViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, SignUpViewModel, IDisposable>>(
            _ => (transport, content, vm) => new SignUpBridge(transport, vm, content: content));
    }

    private sealed record LogEntry(string Category, EventId Id, string Message)
    {
        public override string ToString() => $"{Category} {Id}: {Message}";
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Events { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(RecordingLoggerFactory factory, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (factory.Events) factory.Events.Add(new LogEntry(category, eventId, formatter(state, exception)));
            }
        }
    }

    private static JsonElement ValidationErrors(RunicWindowTestHost<SignUpViewModel> host)
        => host.Root.Snapshot().Json.GetProperty("validation").GetProperty("errors").Clone();

    private static JsonElement Error(JsonElement errors, string member) =>
        errors.EnumerateArray().First(error => error.GetProperty("path")[0].GetString() == member);

    private static string Text(ITranslationContext translations, JsonElement errors, string member) =>
        translations.Resolve(TranslationReference.Parse(Error(errors, member).GetProperty("text").GetRawText())).Text ?? "";
}
