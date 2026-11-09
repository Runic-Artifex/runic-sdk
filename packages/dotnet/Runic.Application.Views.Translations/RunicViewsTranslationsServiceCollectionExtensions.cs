using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Translations;
using Runic.Translations;
using Runic.Translations.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Runic's own English and German text (<see cref="RunicViewsTranslations"/>).</summary>
public static class RunicViewsTranslationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <c>runic-views</c> catalog to the application's Translations context (<c>Runic.Translations.Hosting</c>),
    /// gives the built-in validation attributes its text (<see cref="RunicValidationOptions.TextFor"/>), and keeps
    /// <c>DesktopPlatformTexts.Current</c> in the application's locale from the first window on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog switches with the application's: let the context follow the application's locale, as for any catalog
    /// (<c>locale.Follow(services.GetRequiredService&lt;ITranslationContext&gt;().SetLocaleAsync)</c>). Calling this more
    /// than once registers once.
    /// </para>
    /// <para>
    /// A <see cref="RunicValidationOptions"/> that the application registers itself wins; give it
    /// <see cref="RunicViewsTranslations.ValidationText"/> as the last <c>TextFor</c>. The platform texts are process-wide:
    /// with a singleton context, the <c>Runic.Translations.Hosting</c> default, they follow it. With a scoped context
    /// (<c>options.Lifetime = ServiceLifetime.Scoped</c>), a context per window or request, they follow an application-wide
    /// context of their own from <c>ITranslationContextFactory</c>, which follows <see cref="IRunicApplicationLocale"/>.
    /// A text the platform refuses is logged as event 1260 <c>PlatformTextsRejected</c> (category
    /// <c>Runic.Application.Views.Translations</c>) and the previous texts stay.
    /// </para>
    /// <para>
    /// The catalog has English and German, and <c>ITranslationContext.SupportedLocales</c> lists the locales every catalog
    /// has, so with this package it lists at most <c>en</c> and <c>de</c>. When the application's catalogs have locales
    /// that this catalog lacks, the first window logs them as event 1261 <c>LocalesWithoutRunicViewsText</c>; an
    /// override file per locale (<c>translations/overrides/runic-views/&lt;locale&gt;.rmf2</c>) adds them.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddRunicViewsTranslations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RunicViewsTranslationsRegistration))) return services;
        services.AddSingleton(new RunicViewsTranslationsRegistration(services));
        services.AddRunicTranslations().AddCatalog(RunicViewsTranslations.CatalogRegistration);
        services.TryAddSingleton(new RunicValidationOptions { TextFor = RunicViewsTranslations.ValidationText });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunicWindowOpeningObserver, RunicViewsPlatformTextsObserver>());
        return services;
    }
}

// The application's service collection, read when the first window opens: an application may change the context's
// lifetime with an AddRunicTranslations call after this package registered.
internal sealed class RunicViewsTranslationsRegistration(IServiceCollection services)
{
    // A singleton context is the application's; another one belongs to a scope and is not resolved from the root.
    public bool SharedContext
    {
        get
        {
            var descriptor = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ITranslationContext) && !descriptor.IsKeyedService);
            return descriptor is null || descriptor.Lifetime == ServiceLifetime.Singleton;
        }
    }
}

// Starts following the platform texts when the first window opens: the hosts call window-opening observers on the root
// provider before a window's scope exists, and the application has registered and switched its context by then.
internal sealed partial class RunicViewsPlatformTextsObserver(IServiceProvider services, RunicViewsTranslationsRegistration registration)
    : IRunicWindowOpeningObserver, IDisposable
{
    internal const string LogCategory = "Runic.Application.Views.Translations";
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _registrations = [];
    private ILogger? _logger;
    private bool _started;
    private bool _disposed;

    public void OnWindowOpening(ILoggerFactory? loggerFactory)
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _logger = loggerFactory?.CreateLogger(LogCategory);
            if (registration.SharedContext)
            {
                Follow(services.GetRequiredService<ITranslationContext>());
                return;
            }
        }
        StartApplicationContext();
    }

    // A scoped context belongs to one window; the process-wide texts follow a context of the application's locale.
    private void StartApplicationContext()
    {
        var locale = services.GetService<IRunicApplicationLocale>();
        var creating = services.GetRequiredService<ITranslationContextFactory>().CreateAsync(locale?.Locale);
        if (creating.IsCompletedSuccessfully) Start(creating.Result, locale);
        else _ = StartAsync(creating, locale);
    }

    private async Task StartAsync(ValueTask<TranslationContext> creating, IRunicApplicationLocale? locale)
    {
        try { Start(await creating.ConfigureAwait(false), locale); }
        catch (Exception error) { if (_logger is { } logger) ApplicationContextFailed(logger, error); }
    }

    private void Start(TranslationContext context, IRunicApplicationLocale? locale)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (locale is not null)
            {
                _registrations.Add(locale.Follow(context.SetLocaleAsync));
                // A switch between creating the context and following the locale.
                if (locale.Locale is { } current && !string.Equals(current, context.RequestedLocale, StringComparison.OrdinalIgnoreCase))
                    _ = SwitchAsync(context, current);
            }
            Follow(context);
        }
    }

    private async Task SwitchAsync(TranslationContext context, string locale)
    {
        try { await context.SetLocaleAsync(locale).ConfigureAwait(false); }
        catch (Exception error) { if (_logger is { } logger) ApplicationContextFailed(logger, error); }
    }

    private void Follow(ITranslationContext context)
    {
        var logger = _logger;
        _registrations.Add(RunicViewsTranslations.FollowPlatformTexts(context,
            error => { if (logger is not null) PlatformTextsRejected(logger, error.ParamName ?? "", error); }));
        if (logger is not null && MissingLocales(context) is { Count: > 0 } missing)
            LocalesWithoutRunicViewsText(logger, string.Join(", ", missing));
    }

    // The locales that every other catalog has and runic-views lacks: the ones this package takes out of SupportedLocales.
    internal static IReadOnlyList<string> MissingLocales(ITranslationContext context)
    {
        var views = context.Catalogs.FirstOrDefault(catalog => catalog.CatalogId == RunicViewsTranslations.CatalogId);
        if (views is null) return [];
        HashSet<string>? common = null;
        foreach (var catalog in context.Catalogs)
        {
            if (catalog.CatalogId == RunicViewsTranslations.CatalogId || catalog.SupportedLocales.Count == 0) continue;
            if (common is null) common = new HashSet<string>(catalog.SupportedLocales, StringComparer.Ordinal);
            else common.IntersectWith(catalog.SupportedLocales);
        }
        if (common is null) return [];
        common.ExceptWith(views.SupportedLocales);
        return [.. common.Order(StringComparer.Ordinal)];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var registration in _registrations) registration.Dispose();
            _registrations.Clear();
        }
    }

    [LoggerMessage(EventId = 1260, EventName = "PlatformTextsRejected", Level = LogLevel.Warning,
        Message = "The runic-views text for DesktopPlatformTexts.{Text} is not a valid platform text; the previous texts stay.")]
    private static partial void PlatformTextsRejected(ILogger logger, string text, Exception error);

    [LoggerMessage(EventId = 1261, EventName = "LocalesWithoutRunicViewsText", Level = LogLevel.Warning,
        Message = "The application's catalogs have {Locales}, which Runic.Application.Views.Translations lacks, so " +
            "ITranslationContext.SupportedLocales leaves them out. Add translations/overrides/runic-views/<locale>.rmf2 for each; " +
            "the messages it does not override show in English.")]
    private static partial void LocalesWithoutRunicViewsText(ILogger logger, string locales);

    [LoggerMessage(EventId = 1262, EventName = "PlatformTextsContextFailed", Level = LogLevel.Error,
        Message = "The application-wide Translations context for the platform texts could not be created or switched; " +
            "the platform texts keep their language.")]
    private static partial void ApplicationContextFailed(ILogger logger, Exception error);
}
