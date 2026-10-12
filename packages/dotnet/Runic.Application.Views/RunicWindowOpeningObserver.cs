using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>
/// Called when a Runic Desktop, CS-WebUI or <c>RunicWindowTestHost.Create</c> window opens, before its ViewModel is
/// resolved. An MVVM adapter registers it to configure process-wide state with the application's logger factory.
/// </summary>
/// <remarks>
/// Adapters register implementations, for example with <c>TryAddEnumerable</c>; applications do not. The ReactiveUI
/// adapters register one with <c>AddRunicReactiveModelContext()</c>.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IRunicWindowOpeningObserver
{
    /// <summary>Called before the window resolves its ViewModel.</summary>
    /// <param name="loggerFactory">The application's logger factory, if one is registered.</param>
    void OnWindowOpening(ILoggerFactory? loggerFactory);
}

internal static class RunicWindowOpeningObservers
{
    internal static void Notify(IServiceProvider services, ILoggerFactory? loggerFactory)
    {
        foreach (var observer in services.GetServices<IRunicWindowOpeningObserver>())
            observer.OnWindowOpening(loggerFactory);
    }
}
