using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Platform;

/// <summary>Explicitly selected providers for one presentation. No OS provider is discovered automatically.</summary>
public sealed record PlatformProvider
{
    /// <summary>Gets the optional file picker backend.</summary>
    public IPickerBackend? Files { get; init; }
    /// <summary>Gets the optional text clipboard backend.</summary>
    public ITextClipboard? Clipboard { get; init; }
    /// <summary>Gets the verified owner availability probe.</summary>
    public Func<bool>? OwnerAvailable { get; init; }
    /// <summary>Gets the owner generation, shared by capability snapshots.</summary>
    public Guid? Generation { get; init; }
    /// <summary>Gets an optional presentation dispatcher factory.</summary>
    public Func<PresentationLifetime, IUiDispatcher>? CreateDispatcher { get; init; }
}

/// <summary>Drains scoped native operations before a host destroys its presentation.</summary>
public sealed class PlatformPresentation : IApplicationPresentationLifetime
{
    private readonly PresentationLifetime _lifetime;

    /// <summary>Initializes all selected service wrappers so provider resources participate in shutdown.</summary>
    public PlatformPresentation(PresentationLifetime lifetime, PresentationFiles files, PresentationClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clipboard);
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public ValueTask StopAsync() => _lifetime.StopAsync();
}

/// <summary>Registers shared platform service contracts without loading native providers.</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>Registers one platform runtime per bridge presentation scope.</summary>
    public static IServiceCollection AddRunicPlatform(this IServiceCollection services,
        Func<IServiceProvider, PlatformProvider>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(item => item.ServiceType == typeof(PlatformPresentation)))
            throw new InvalidOperationException("Platform services are already registered for this presentation.");
        services.AddScoped(provider => configure?.Invoke(provider) ?? new PlatformProvider());
        services.AddScoped(provider =>
        {
            var selected = provider.GetRequiredService<PlatformProvider>();
            return new PresentationLifetime(selected.OwnerAvailable, selected.Generation);
        });
        services.AddScoped<PlatformPresentation>();
        services.AddScoped<IApplicationPresentationLifetime>(provider => provider.GetRequiredService<PlatformPresentation>());
        services.AddScoped(provider => new PresentationFiles(provider.GetRequiredService<PresentationLifetime>(),
            provider.GetRequiredService<PlatformProvider>().Files));
        services.AddScoped<IFileDialogs>(provider => provider.GetRequiredService<PresentationFiles>());
        services.AddScoped(provider => new PresentationClipboard(provider.GetRequiredService<PresentationLifetime>(),
            provider.GetRequiredService<PlatformProvider>().Clipboard));
        services.AddScoped<ITextClipboard>(provider => provider.GetRequiredService<PresentationClipboard>());
        services.AddScoped<IPlatformCapabilities, Capabilities>();
        services.AddScoped<IUiDispatcher>(provider =>
            provider.GetRequiredService<PlatformProvider>().CreateDispatcher?.Invoke(provider.GetRequiredService<PresentationLifetime>())
            ?? new UnavailableDispatcher());
        return services;
    }

    private sealed class Capabilities(PresentationFiles files, PresentationClipboard clipboard) : IPlatformCapabilities
    {
        public CapabilitySnapshot GetSnapshot()
        {
            var snapshot = files.GetSnapshot();
            return snapshot with { Statuses = snapshot.Statuses.SetItems(clipboard.GetSnapshot().Statuses) };
        }
    }

    private sealed class UnavailableDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => false;
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("This presentation has no native UI dispatcher.");
        }
    }
}
