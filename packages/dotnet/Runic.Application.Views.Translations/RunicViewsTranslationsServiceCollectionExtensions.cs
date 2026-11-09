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
    /// <c>DesktopPlatformTexts.Current</c> in the context's locale from the first window on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog switches with the application's: let the context follow the application's locale, as for any catalog
    /// (<c>locale.Follow(services.GetRequiredService&lt;ITranslationContext&gt;().SetLocaleAsync)</c>). Calling this more
    /// than once registers once.
    /// </para>
    /// <para>
    /// A <see cref="RunicValidationOptions"/> that the application registers itself wins; give it
    /// <see cref="RunicViewsTranslations.ValidationText"/> as the last <c>TextFor</c>. The platform texts follow a singleton
    /// context, the <c>Runic.Translations.Hosting</c> default; with a scoped context, call
    /// <see cref="RunicViewsTranslations.FollowPlatformTexts"/> with the context to follow. A text the platform refuses is
    /// logged as event 1260 <c>PlatformTextsRejected</c> (category <c>Runic.Application.Views.Translations</c>) and the
    /// previous texts stay.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddRunicViewsTranslations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RunicViewsTranslationsMarker))) return services;
        services.AddSingleton<RunicViewsTranslationsMarker>();
        services.AddRunicTranslations().AddCatalog(RunicViewsTextCatalog.Registration, context => new RunicViewsText(context));
        services.TryAddSingleton(new RunicValidationOptions { TextFor = RunicViewsTranslations.ValidationText });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunicWindowOpeningObserver, RunicViewsPlatformTextsObserver>());
        return services;
    }

    private sealed class RunicViewsTranslationsMarker;
}

// Starts following the platform texts when the first window opens: the hosts call window-opening observers before a
// window resolves its ViewModel, and the application has registered and switched its context by then.
internal sealed partial class RunicViewsPlatformTextsObserver(IServiceProvider services) : IRunicWindowOpeningObserver, IDisposable
{
    internal const string LogCategory = "Runic.Application.Views.Translations";
    private readonly Lock _gate = new();
    private IDisposable? _following;
    private bool _disposed;

    public void OnWindowOpening(ILoggerFactory? loggerFactory)
    {
        lock (_gate)
        {
            if (_following is not null || _disposed) return;
            var logger = loggerFactory?.CreateLogger(LogCategory);
            _following = RunicViewsTranslations.FollowPlatformTexts(services.GetRequiredService<ITranslationContext>(),
                error => { if (logger is not null) PlatformTextsRejected(logger, error.ParamName ?? "", error); });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _following?.Dispose();
            _following = null;
        }
    }

    [LoggerMessage(EventId = 1260, EventName = "PlatformTextsRejected", Level = LogLevel.Warning,
        Message = "The runic-views text for DesktopPlatformTexts.{Text} is not a valid platform text; the previous texts stay.")]
    private static partial void PlatformTextsRejected(ILogger logger, string text, Exception error);
}
