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
    /// Whether the window's Bridge error replies carry exception detail
    /// (<see cref="WindowContentSessionOptions.IncludeFailureDetail"/>). <see langword="null"/>, the default,
    /// includes it only in a <c>Development</c> environment.
    /// </summary>
    public bool? IncludeFailureDetail { get; init; }

    /// <summary>
    /// How the window's Bridges write validation (<see cref="WindowContentSessionOptions.Validation"/>), such as
    /// <see cref="RunicValidationOptions.TextFor"/>. <c>RunicWindowTestHost.Create(services)</c> takes the services'
    /// <see cref="RunicValidationOptions"/> when this is <see langword="null"/>, and each Window that
    /// <c>RunicWindowTestHost.CreateWindowAsync</c> opens takes its scope's, as on Runic Desktop and CS-WebUI.
    /// </summary>
    public RunicValidationOptions? Validation { get; init; }

    /// <summary>
    /// Whether the window's requests run in the culture of its locale
    /// (<see cref="WindowContentSessionOptions.FlowCultureFromLocale"/>), for Windows whose
    /// <see cref="RunicWindowOptions.FlowCultureFromLocale"/> is <see langword="null"/>. <see langword="null"/>, the
    /// default, keeps the process's cultures.
    /// </summary>
    public bool? FlowCultureFromLocale { get; init; }

    /// <summary>
    /// The window's own locale when the host starts, a BCP 47 tag such as <c>de</c> or <c>ar</c>, as a Window whose
    /// ViewModel sets <see cref="IRunicWindowLocale.Locale"/> before it opens: the page's first <c>hello</c> receives
    /// it, and with <see cref="FlowCultureFromLocale"/> the window's requests run in its culture. The window keeps it
    /// when the application's locale changes. <see langword="null"/>, the default, follows the application's locale
    /// (<see cref="ApplicationLocale"/>).
    /// </summary>
    /// <remarks>
    /// It means the same on every host: the constructors, <c>RunicWindowTestHost.Create</c>, and every Window that
    /// <c>RunicWindowTestHost.CreateWindowAsync</c> opens, including the Windows its ViewModels open later. A locale the
    /// ViewModel sets through <see cref="IRunicWindowLocale"/> before the window opens takes precedence. To start the
    /// whole application in a language and switch it later, set <see cref="ApplicationLocale"/> instead. A value that
    /// is not 1 to 64 letters, digits and <c>-</c>, starting with a letter, throws <see cref="ArgumentException"/>.
    /// </remarks>
    public string? Locale { get; init; }

    /// <summary>
    /// The application's locale (<see cref="IRunicApplicationLocale"/>) while the host runs, such as <c>de</c>: the
    /// host sets it before the window opens, as an application does at start, and every window without a
    /// <see cref="Locale"/> of its own follows it and any later switch. <see langword="null"/>, the default, leaves
    /// the application's locale as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RunicWindowTestHost.CreateWindowAsync</c> and <c>RunicWindowTestHost.Create</c> set the
    /// <see cref="IRunicApplicationLocale"/> that the services register, which <c>AddRunicWindowService()</c> registers
    /// as a singleton, so its followers (such as a Translations context) switch too. Disposing the host, or the Window
    /// that <c>CreateWindowAsync</c> returned, sets it back to the locale it had before, also when the test switched it
    /// since: tests that share one provider do not inherit the language. A constructor's host, and a
    /// <c>Create</c> host whose services register none, start their own <see cref="RunicWindowTestHost{TViewModel}.ApplicationLocale"/>
    /// in it.
    /// </para>
    /// <para>
    /// <c>CreateWindowAsync</c> requires a registered <see cref="IRunicApplicationLocale"/> for it and awaits the switch.
    /// The constructors and <c>Create</c> are synchronous and wait for it and for the switch back. A value that is not
    /// a locale tag throws <see cref="ArgumentException"/>.
    /// </para>
    /// </remarks>
    public string? ApplicationLocale { get; init; }

    /// <summary>
    /// The largest Bridge input of the window, in UTF-8 bytes
    /// (<see cref="WindowContentSessionOptions.MaximumInputBytes"/>). <see langword="null"/>, the default, is 1 MiB.
    /// </summary>
    public int? MaximumInputBytes { get; init; }

    /// <summary>
    /// The largest upload of the window (<see cref="WindowContentSessionOptions.MaximumUploadBytes"/>), which
    /// <c>UploadAsync</c> also applies. <see langword="null"/>, the default, is 256 MiB.
    /// </summary>
    public long? MaximumUploadBytes { get; init; }

    /// <summary>
    /// How long a driver waits for a state publication. Delivery runs off the calling
    /// thread, so this is real time. Defaults to five seconds.
    /// </summary>
    public TimeSpan PublicationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
