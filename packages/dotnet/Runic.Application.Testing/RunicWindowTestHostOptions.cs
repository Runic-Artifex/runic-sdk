using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>Configures a <see cref="RunicWindowTestHost{TViewModel}"/>.</summary>
public sealed class RunicWindowTestHostOptions
{
    /// <summary>
    /// The generated root route. Defaults to the ViewModel's generated name: <c>shell</c>
    /// for <c>ShellViewModel</c>.
    /// </summary>
    public string? RootRoute { get; init; }

    /// <summary>Resolves .NET Views for presented content.</summary>
    public IRunicViewLocator? ViewLocator { get; init; }

    /// <summary>Cancels window-owned operations when signalled.</summary>
    public CancellationToken OperationShutdown { get; init; }

    /// <summary>The model context the window graph must share, if any.</summary>
    public IRunicModelContext? ModelContext { get; init; }

    /// <summary>Receives the window's Bridge, operation and mount logs.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// The window's clock, which interaction deadlines and close timeouts use. Defaults to a
    /// new <see cref="FakeTimeProvider"/>, so time passes only when the test advances it.
    /// Register the same instance in the application's services to control its time too.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Creates content reference ids and interaction request ids. Defaults to the sequence
    /// <c>1</c>, <c>2</c>, ..., so content routes are <c>content1</c>, <c>content2</c>, ...
    /// </summary>
    public Func<string>? CreateId { get; init; }

    /// <summary>
    /// How long a driver waits for a state publication. Delivery runs off the calling
    /// thread, so this is real time. Defaults to five seconds.
    /// </summary>
    public TimeSpan PublicationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
