using Microsoft.Extensions.Logging;
using Runic.Navigation;

namespace Runic.Application.Views;

/// <summary>Configures a <see cref="WindowContentSession"/>.</summary>
public sealed class WindowContentSessionOptions
{
    /// <summary>The default of <see cref="MaximumInputBytes"/>: 1 MiB.</summary>
    public const int DefaultMaximumInputBytes = 1_048_576;

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
    /// <see cref="TimeProvider.System"/>; tests pass a fake clock.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Creates the ids of content references (route <c>content{id}</c>) and interaction
    /// requests. Each id must be unique within the session and contain only ASCII letters
    /// and digits. Defaults to random GUIDs; tests pass a sequence for stable routes.
    /// </summary>
    public Func<string>? CreateId { get; init; }

    /// <summary>
    /// Whether this window's Bridge error replies carry the exception type, message and stack, which the
    /// TypeScript runtime exposes as <c>BridgeError.detail</c> and the Vite plugin shows in DevTools.
    /// <see langword="null"/>, the default, includes them only when the host environment is
    /// <c>Development</c>: <c>DOTNET_ENVIRONMENT</c>, or else <c>ASPNETCORE_ENVIRONMENT</c>, which
    /// <c>dotnet runic dev</c> sets. Detail can contain file paths and application data. The session reads
    /// the value once, when it is created.
    /// </summary>
    public bool? IncludeFailureDetail { get; init; }

    /// <summary>
    /// The largest Bridge input, in UTF-8 bytes, that this window decodes: a setter value, a command argument,
    /// a checked write, an operation start, and an interaction request or reply. A larger one is answered as
    /// invalid input (<c>rejected</c> with reason <c>invalid-input</c>, or the interaction routes'
    /// <c>invalid-request</c>) and does not reach the model. <see langword="null"/>, the default, is
    /// <see cref="DefaultMaximumInputBytes"/> (1 MiB). A value must be between 1 KiB (1 024) and 64 MiB
    /// (67 108 864); the session throws <see cref="ArgumentOutOfRangeException"/> for another one. The
    /// session reads the value once, when it is created.
    /// </summary>
    public int? MaximumInputBytes { get; init; }
}
