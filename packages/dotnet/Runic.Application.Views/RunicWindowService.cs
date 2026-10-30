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

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> as <see cref="OpenWindowAsync{TWindow}"/> does, and hands
    /// <paramref name="parameter"/> to the new Window's scope, where its ViewModel takes
    /// <see cref="IRunicWindowParameter{TParameter}"/>, as
    /// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow, TParameter}"/> does.
    /// </summary>
    /// <remarks>
    /// <code>
    /// await _windows.OpenWindowAsync&lt;PartWindow, PartRequest&gt;(new PartRequest(world, part));
    ///
    /// public sealed class PartViewModel(IRunicWindowParameter&lt;PartRequest&gt; request) { ... }
    /// </code>
    /// The default implementation, for a test fake that does not override it, throws <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <param name="parameter">The value the new Window's scope receives.</param>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>The open Window, released by the service once it closes.</returns>
    /// <exception cref="InvalidOperationException">No host has opened a Window with these services yet.</exception>
    /// <exception cref="RunicWindowConfigurationException">A check failed before anything opened.</exception>
    ValueTask<TWindow> OpenWindowAsync<TWindow, TParameter>(TParameter parameter, RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class =>
        throw new NotSupportedException($"{GetType().Name} does not open Windows with a parameter.");
}

/// <summary>
/// The parameter that <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow, TParameter}"/> or
/// <see cref="IRunicWindowService.OpenWindowAsync{TWindow, TParameter}"/> handed to the Window whose scope resolves it.
/// Take it in the constructor of the Window's ViewModel, or of a scoped service the ViewModel uses.
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope. In a test, pass a
/// <see cref="RunicWindowParameter{TParameter}"/> to a ViewModel built by hand.
/// </remarks>
/// <typeparam name="TParameter">The parameter's type, as the open named it.</typeparam>
public interface IRunicWindowParameter<out TParameter> where TParameter : class
{
    /// <summary>The parameter the Window was opened with.</summary>
    /// <exception cref="InvalidOperationException">
    /// The Window was opened without a parameter of this type, or the service was resolved outside a Window's scope.
    /// </exception>
    TParameter Value { get; }
}

/// <summary>A fixed <see cref="IRunicWindowParameter{TParameter}"/>, for a ViewModel that a test builds by hand.</summary>
/// <param name="value">The parameter.</param>
public sealed class RunicWindowParameter<TParameter>(TParameter value) : IRunicWindowParameter<TParameter> where TParameter : class
{
    /// <inheritdoc />
    public TParameter Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
}

// The parameter of the open that created this scope.
internal sealed class RunicWindowParameter
{
    private object? _value;

    internal void Set(object value)
    {
        if (Interlocked.CompareExchange(ref _value, value, null) is not null)
            throw new InvalidOperationException("The Window's scope already has a parameter.");
    }

    internal TParameter Get<TParameter>() where TParameter : class => Volatile.Read(ref _value) switch
    {
        TParameter value => value,
        null => throw new InvalidOperationException(
            $"IRunicWindowParameter<{typeof(TParameter).Name}> was resolved in a scope without a parameter. Open the Window with " +
            $"OpenWindowAsync<TWindow, {typeof(TParameter).Name}>(parameter), and resolve it in that Window's scope."),
        var other => throw new InvalidOperationException(
            $"The Window was opened with a {other.GetType().Name} parameter, not a {typeof(TParameter).Name}."),
    };
}

internal sealed class ScopedWindowParameter<TParameter>(RunicWindowParameter slot) : IRunicWindowParameter<TParameter>
    where TParameter : class
{
    public TParameter Value => slot.Get<TParameter>();
}

/// <summary>Registers <see cref="IRunicWindowService"/> for dependency injection.</summary>
public static class RunicWindowServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRunicWindowService"/> as a singleton, and <see cref="IRunicWindowContext"/> and
    /// <see cref="IRunicWindowParameter{TParameter}"/> per scope. Existing registrations are kept.
    /// </summary>
    /// <remarks>
    /// The window service follows the host, such as <c>RunicDesktopHost</c> or <c>RunicCsWebUiHost</c>, that most
    /// recently opened a Window with these services. A host binds it before it resolves that Window's ViewModel, so the
    /// service is ready in every ViewModel constructor and command. A service that uses it before the application
    /// opens its first Window gets an <see cref="InvalidOperationException"/>. The window context of each Window's
    /// scope is bound to that Window before its ViewModel is resolved.
    /// </remarks>
    public static IServiceCollection AddRunicWindowService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static provider => new RunicWindowService(provider));
        services.TryAddSingleton<IRunicWindowService>(static provider => provider.GetRequiredService<RunicWindowService>());
        services.TryAddScoped(static _ => new RunicWindowContext());
        services.TryAddScoped<IRunicWindowContext>(static provider => provider.GetRequiredService<RunicWindowContext>());
        services.TryAddScoped(static _ => new RunicWindowParameter());
        services.TryAddScoped(typeof(IRunicWindowParameter<>), typeof(ScopedWindowParameter<>));
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
        var window = await Host<TWindow>().OpenWindowAsync<TWindow>(options, cancellationToken).ConfigureAwait(false);
        _ = ReleaseWhenClosedAsync(window);
        return window;
    }

    public async ValueTask<TWindow> OpenWindowAsync<TWindow, TParameter>(TParameter parameter, RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class
    {
        var window = await Host<TWindow>().OpenWindowAsync<TWindow, TParameter>(parameter, options, cancellationToken)
            .ConfigureAwait(false);
        _ = ReleaseWhenClosedAsync(window);
        return window;
    }

    private IRunicWindowHost Host<TWindow>() => Volatile.Read(ref _host) ?? throw new InvalidOperationException(
        $"No Runic host has opened a Window yet, so {typeof(TWindow).Name} cannot open. Open the first Window with " +
        "host.OpenWindowAsync<TWindow>() in RunicDesktopHost.Run or RunicCsWebUiHost.Run; ViewModels and services " +
        "can then open more with IRunicWindowService.");

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
