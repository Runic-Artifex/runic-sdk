using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>Configures a <see cref="WindowContentSession"/>.</summary>
public sealed class WindowContentSessionOptions
{
    /// <summary>Resolves .NET Views for presented content.</summary>
    public IRunicViewLocator? ViewLocator { get; init; }

    /// <summary>Cancels window-owned operations when signalled.</summary>
    public CancellationToken OperationShutdown { get; init; }

    /// <summary>The window's root ViewModel, if any.</summary>
    public object? RootModel { get; init; }

    /// <summary>
    /// An application-owned context, such as the scoped DI context, that the window graph
    /// must share. See the <see cref="WindowContentSession"/> constructor.
    /// </summary>
    public IRunicModelContext? ModelContext { get; init; }

    /// <summary>Creates the loggers of this window's Bridges, operations and mounts.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// The clock of interaction deadlines and close timeouts. Defaults to
    /// <see cref="TimeProvider.System"/>; tests pass a manual clock.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Creates the ids of content references (route <c>content{id}</c>) and interaction
    /// requests. Each id must be unique within the session and contain only ASCII letters
    /// and digits. Defaults to random GUIDs; tests pass a sequence for stable routes.
    /// </summary>
    public Func<string>? CreateId { get; init; }
}
