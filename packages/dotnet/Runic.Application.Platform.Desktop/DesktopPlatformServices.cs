using Microsoft.Extensions.DependencyInjection;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Platform.Desktop;

/// <summary>Explicit Desktop integration without implicit OS provider dependencies.</summary>
public static class DesktopPlatformServiceCollectionExtensions
{
    /// <summary>Registers one verified owner and explicitly selected providers per presentation scope.</summary>
    public static IServiceCollection AddRunicDesktopPlatform(this IServiceCollection services,
        Func<DesktopWindow?> window,
        Func<DesktopNativeOwner, PlatformProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddRunicPlatform(_ =>
        {
            var owner = new DesktopNativeOwner(window);
            var selected = configure(owner);
            return selected with
            {
                OwnerAvailable = () => owner.IsAvailable,
                Generation = owner.Generation,
                CreateDispatcher = lifetime => new NativeDispatcher(lifetime, owner),
            };
        });
    }

    private sealed class NativeDispatcher(PresentationLifetime lifetime, DesktopNativeOwner owner) : IUiDispatcher
    {
        public bool CheckAccess() => !lifetime.IsClosing && owner.CheckAccess();
        public async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            cancellationToken.ThrowIfCancellationRequested();
            using var operation = lifetime.TryBeginOperation() ?? throw new OwnerClosedException();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Shutdown);
            await owner.InvokeAsync(_ =>
            {
                linked.Token.ThrowIfCancellationRequested();
                action();
            }, linked.Token).ConfigureAwait(false);
        }
    }
}
