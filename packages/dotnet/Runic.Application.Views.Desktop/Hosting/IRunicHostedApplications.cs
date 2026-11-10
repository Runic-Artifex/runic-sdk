using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Starts other Runic applications as <b>guests</b> and shows their Windows in native windows of this application, the
/// <b>host</b> (the Runic host protocol, <c>runic-host/1</c>, in window mode).
/// </summary>
/// <remarks>
/// <para>
/// Get it from <see cref="RunicDesktopHost.HostedApplications"/>, or from the services after
/// <see cref="RunicHostedApplicationServiceCollectionExtensions.AddRunicHostedApplications"/>:
/// </para>
/// <code>
/// var applications = host.HostedApplications;
/// applications.WindowOpened += (_, e) =&gt; Console.WriteLine($"{e.Window.Application.Guest.Name} opened {e.Window.Type}");
/// await using var tool = await applications.StartAsync(new RunicGuestStartInfo("/opt/tool/Tool", ["hosted"]));
/// int exitCode = await tool.Exited;
/// </code>
/// <para>
/// Each <see cref="StartAsync"/> starts a guest process of its own, which serves this host session only. The guest must
/// support hosted mode through a parameter it defines (<see cref="RunicGuestOptions"/>), which the start information
/// names. For each of its Windows the host opens a native window (<see cref="RunicHostedWindow.Presentation"/>) that
/// loads the guest's page from the guest's own loopback address, with a web view data store of the session's own that is
/// not persisted, and without this application's credentials. <see cref="RunicHostedApplicationOptions.Policy"/> decides
/// which windows open and what they may do; by default every window opens.
/// </para>
/// <para>
/// When the Desktop host shuts down, or <see cref="RunicDesktopHost.Run(IServiceProvider, Runic.Desktop.DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
/// returns, every guest still running is stopped. The protocol and this API are experimental; see
/// <c>docs/experimental.md</c>.
/// </para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public interface IRunicHostedApplications
{
    /// <summary>The guests that are running, in the order they started.</summary>
    IReadOnlyList<RunicHostedApplication> Applications { get; }

    /// <summary>
    /// Raised when a window of any guest opened, on a thread-pool thread. Add the handler before
    /// <see cref="StartAsync"/>: a guest can open its first window before the start returns.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowOpened"/> and <see cref="WindowClosed"/> are raised one at a time, in order: a window's
    /// <see cref="WindowClosed"/> follows its <see cref="WindowOpened"/>, and a window that never opened for the host, such
    /// as one whose guest went away while it opened, raises neither.
    /// </remarks>
    event EventHandler<RunicHostedWindowEventArgs>? WindowOpened;

    /// <summary>Raised when a window of any guest closed, for any reason, on a thread-pool thread.</summary>
    event EventHandler<RunicHostedWindowEventArgs>? WindowClosed;

    /// <summary>Starts a guest application in hosted mode and completes the handshake with it.</summary>
    /// <param name="startInfo">The guest's executable and the arguments that select its hosted mode.</param>
    /// <param name="options">Timeouts and the window policy; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the start; the guest's process tree is then ended.</param>
    /// <returns>The running guest. Its Windows open in this application's windows from now on.</returns>
    /// <exception cref="RunicHostedApplicationException">
    /// The guest could not be hosted, and its process tree was ended. <see cref="RunicHostedApplicationException.Code"/>
    /// is <c>guest-start-failed</c> (the executable could not be started, or not in a job object on Windows; see
    /// <see cref="Exception.InnerException"/>), <c>guest-start-timeout</c>, <c>guest-exited</c>, <c>guest-not-hostable</c>,
    /// <c>guest-refused</c> (with <see cref="RunicHostedApplicationException.GuestCode"/>) or <c>protocol-unsupported</c>.
    /// </exception>
    /// <exception cref="ArgumentException">On Windows, the executable is a batch file (<c>.bat</c>, <c>.cmd</c>), which only <c>cmd.exe</c> runs.</exception>
    /// <exception cref="InvalidOperationException">
    /// The Desktop host is shutting down, or runs serve-only or hosted itself. A shutdown or disposal of the Desktop host
    /// while the start is pending also fails it this way, and ends the guest's process tree.
    /// </exception>
    ValueTask<RunicHostedApplication> StartAsync(RunicGuestStartInfo startInfo, RunicHostedApplicationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>How a host starts a guest application (the Runic host protocol, <c>runic-host/1</c>).</summary>
/// <param name="FileName">
/// The guest's executable: an absolute path, a path relative to the current directory, or a name to look up in
/// <c>PATH</c>. It is started directly, never through a shell.
/// </param>
/// <param name="Arguments">
/// Its arguments, passed as they are, such as the hidden command <c>hosted</c> through which the guest reaches its hosted
/// mode. They are visible to other processes: never pass a secret.
/// </param>
/// <remarks>
/// The guest inherits this process's environment with <see cref="Environment"/> applied, and without
/// <c>RUNIC_APPLICATION_SERVE_ONLY</c> and <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c>. Its standard input and output are
/// the host's, for the protocol; its other output and its standard error go to this application's log at the
/// <c>Debug</c> level (category <c>Runic.Application.Views.Desktop.Hosting</c>). Experimental; see
/// <c>docs/experimental.md</c>.
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicGuestStartInfo(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The guest's executable.</summary>
    public string FileName { get; init => field = RequireText(value, nameof(FileName)); } = RequireText(FileName, nameof(FileName));

    /// <summary>The guest's arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init => field = RequireArguments(value, nameof(Arguments)); } =
        RequireArguments(Arguments, nameof(Arguments));

    /// <summary>The guest's working directory, or <see langword="null"/>, the default, for this process's current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables to set for the guest, and those with a <see langword="null"/> value to remove; the others are
    /// inherited. Never pass a secret: other processes of the user can read a process's environment.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>
    /// Whether the user started the guest directly, such as from the host's own menu or launcher;
    /// <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default the guest's windows start <i>guest-activated</i> (<see cref="RunicHostedServiceRequest.IsGuestActivated"/>):
    /// the guest opened them, so the host refuses their clipboard reads, clipboard writes and launches until the user
    /// switches to the window, clicks its native menu bar, picks a context menu item or drops files onto it. A link the user
    /// clicks in a guest's fresh first window is refused until then.
    /// </para>
    /// <para>
    /// With <see langword="true"/>, the first activation of the guest's first window counts as the user's, as if the user
    /// had switched to it. Set it only when the user's own action started the guest just now. The trade-off: the guest
    /// decides when it opens that window, so it can open it later, while the user works in another window, and the host
    /// still treats it as the user's. Later windows, and the first one once the guest presents, shows, maximizes or makes it
    /// full screen, are guest-activated as usual.
    /// </para>
    /// </remarks>
    public bool UserStarted { get; init; }

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static IReadOnlyList<string> RequireArguments(IReadOnlyList<string> value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Any(static argument => argument is null)) throw new ArgumentException("An argument is null.", name);
        return value;
    }
}

/// <summary>How a host runs one guest application (the Runic host protocol, <c>runic-host/1</c>).</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedApplicationOptions
{
    /// <summary>
    /// How long the guest may take to write its channel line and answer the handshake; 30 seconds by default. After it, the
    /// start fails with <c>guest-start-timeout</c> and the guest's process tree is ended.
    /// </summary>
    public TimeSpan StartTimeout { get; init => field = RequirePositive(value, nameof(StartTimeout)); } = Hosting.HostProtocol.DefaultStartTimeout;

    /// <summary>
    /// How long a stopping guest may take to exit after the host asked it to shut down; 10 seconds by default. After it,
    /// its process tree is ended.
    /// </summary>
    public TimeSpan StopTimeout { get; init => field = RequirePositive(value, nameof(StopTimeout)); } = Hosting.HostProtocol.DefaultStopTimeout;

    /// <summary>
    /// Decides which windows the guest may open and what it may do with them; <see langword="null"/>, the default, allows
    /// everything.
    /// </summary>
    public IRunicHostedWindowPolicy? Policy { get; init; }

    /// <summary>
    /// The host's name the guest learns, which its <see cref="RunicGuestOptions.AcceptHost"/> may check; by default this
    /// application's entry assembly name.
    /// </summary>
    public string? HostName { get; init; }

    /// <summary>The host's version the guest learns; by default this application's informational version.</summary>
    public string? HostVersion { get; init; }

    private static TimeSpan RequirePositive(TimeSpan value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, name);
        return value;
    }
}

/// <summary>A guest application as its handshake named it.</summary>
/// <param name="Name">The application's name.</param>
/// <param name="Version">The application's version.</param>
/// <param name="ApplicationId">
/// The application's identity, such as its reverse-DNS name, or <see langword="null"/>. A host may key what it keeps about
/// a guest, such as its windows' placements, by it.
/// </param>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicGuestInfo(string Name, string Version, string? ApplicationId);

/// <summary>A hosted window that opened or closed.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedWindowEventArgs : EventArgs
{
    /// <summary>Creates the arguments for a window.</summary>
    /// <param name="window">The window.</param>
    public RunicHostedWindowEventArgs(RunicHostedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Window = window;
    }

    /// <summary>The window.</summary>
    public RunicHostedWindow Window { get; }
}

/// <summary>Registers <see cref="IRunicHostedApplications"/> (the Runic host protocol, <c>runic-host/1</c>).</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public static class RunicHostedApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRunicHostedApplications"/> as the <see cref="RunicDesktopHost.HostedApplications"/> of the
    /// <see cref="RunicDesktopHost"/> that runs with these services.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// Resolve it after <see cref="RunicDesktopHost.Run(IServiceProvider, Runic.Desktop.DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
    /// started, such as in a Window's ViewModel; before that it throws <see cref="InvalidOperationException"/>. An existing
    /// registration is kept.
    /// </remarks>
    public static IServiceCollection AddRunicHostedApplications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRunicHostedApplications>(static provider =>
            (RunicDesktopHost.ForServices(provider) ?? throw new InvalidOperationException(
                "No RunicDesktopHost runs with these services yet: resolve IRunicHostedApplications after RunicDesktopHost.Run started."))
            .HostedApplications);
        return services;
    }
}
