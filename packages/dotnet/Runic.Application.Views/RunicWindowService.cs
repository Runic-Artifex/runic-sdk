using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>
/// Opens Windows from ViewModels and application services, on the host that runs the application. Take it in a
/// constructor and open a Window from a command:
/// <code>
/// [RelayCommand]
/// private async Task OpenProjectAsync() =&gt; await _windows.OpenWindowAsync&lt;ProjectWindow&gt;();
/// </code>
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it as a singleton, so
/// ViewModels in every Window's scope and singleton services can take it. It works the same on every host, because
/// it calls <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> on the host that opened the
/// application's Windows. In tests, register a fake that records the requested Window type instead.
/// </remarks>
public interface IRunicWindowService
{
    /// <summary>
    /// Creates <typeparamref name="TWindow"/>, resolves its ViewModel in a new DI scope, and opens it, as
    /// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> does.
    /// </summary>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>
    /// The open Window. The service releases it once it closes; awaiting <see cref="IRunicWindow.WaitForCloseAsync"/>
    /// or disposing it earlier is optional.
    /// </returns>
    /// <exception cref="InvalidOperationException">No host has opened a Window with these services yet.</exception>
    /// <exception cref="RunicWindowConfigurationException">A check failed before anything opened.</exception>
    ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new();
}

/// <summary>Registers <see cref="IRunicWindowService"/> for dependency injection.</summary>
public static class RunicWindowServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IRunicWindowService"/> as a singleton. An existing registration is kept.</summary>
    /// <remarks>
    /// The service follows the host, such as <c>RunicDesktopHost</c> or <c>RunicCsWebUiHost</c>, that most recently
    /// opened a Window with these services. A host binds it before it resolves that Window's ViewModel, so the
    /// service is ready in every ViewModel constructor and command. A service that uses it before the application
    /// opens its first Window gets an <see cref="InvalidOperationException"/>.
    /// </remarks>
    public static IServiceCollection AddRunicWindowService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static provider => new RunicWindowService(provider));
        services.TryAddSingleton<IRunicWindowService>(static provider => provider.GetRequiredService<RunicWindowService>());
        return services;
    }
}

internal sealed class RunicWindowService(IServiceProvider services) : IRunicWindowService
{
    private IRunicWindowHost? _host;

    // Called by every OpenWindowAsync before it creates the Window's scope.
    internal void Bind(IRunicWindowHost host) => Volatile.Write(ref _host, host);

    public async ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
    {
        var host = Volatile.Read(ref _host) ?? throw new InvalidOperationException(
            $"No Runic host has opened a Window yet, so {typeof(TWindow).Name} cannot open. Open the first Window with " +
            "host.OpenWindowAsync<TWindow>() in RunicDesktopHost.Run or RunicCsWebUiHost.Run; ViewModels and services " +
            "can then open more with IRunicWindowService.");
        var window = await host.OpenWindowAsync<TWindow>(options, cancellationToken).ConfigureAwait(false);
        _ = ReleaseWhenClosedAsync(window);
        return window;
    }

    // A Window opened from a command usually has no owner that awaits it; closing it must still release its scope.
    // Disposing an already released Window does nothing.
    private async Task ReleaseWhenClosedAsync(IRunicWindow window)
    {
        try { await window.WaitForCloseAsync().ConfigureAwait(false); }
        catch (Exception error) { ReleaseFailed(window, error); }
        try { await window.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { ReleaseFailed(window, error); }
    }

    private void ReleaseFailed(IRunicWindow window, Exception error)
    {
        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger(RunicViewsTelemetry.LogCategory)
            ?? TraceFallbackLogger.Instance;
        ViewsLog.WindowReleaseFailed(logger, error, window.GetType().Name, error.GetType().Name);
    }
}
