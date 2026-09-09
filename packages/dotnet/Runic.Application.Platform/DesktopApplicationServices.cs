using Microsoft.Extensions.DependencyInjection;
using Runic.Platform;

namespace Runic.Application.Platform;

/// <summary>Registers application-scoped services independently of presentation scopes and native windows.</summary>
public static class DesktopApplicationServiceCollectionExtensions
{
    /// <summary>The container creates and asynchronously disposes each selected singleton provider.</summary>
    public static IServiceCollection AddRunicDesktopServices(this IServiceCollection services,
        Func<IServiceProvider, IDesktopSettings> settings,
        Func<IServiceProvider, IDesktopNotifications> notifications)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(notifications);
        if (services.Any(d => d.ServiceType == typeof(IDesktopSettings) || d.ServiceType == typeof(IDesktopNotifications)))
            throw new InvalidOperationException("Desktop application services are already registered.");
        services.AddSingleton(settings);
        services.AddSingleton(notifications);
        services.AddSingleton<IApplicationStoppingParticipant, DesktopServicesLifetime>();
        return services;
    }
}

internal sealed class DesktopServicesLifetime(IDesktopSettings settings, IDesktopNotifications notifications) : IApplicationStoppingParticipant
{
    public async ValueTask StopAsync()
    {
        try { await settings.DisposeAsync().ConfigureAwait(false); }
        finally { await notifications.DisposeAsync().ConfigureAwait(false); }
    }
}
