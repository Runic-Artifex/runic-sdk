using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Runs a Runic Desktop application in <b>hosted mode</b>: another Runic application, the host, started it and shows
/// its Windows in native windows of its own (the Runic host protocol, <c>runic-host/1</c>). Pass it as
/// <see cref="RunicDesktopHostOptions.Guest"/> to
/// <see cref="RunicDesktopHost.Run(IServiceProvider, Runic.Desktop.DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>.
/// </summary>
/// <remarks>
/// <para>
/// The application decides when it can be hosted: it defines its own parameter, such as a hidden command
/// <c>hosted</c>, and runs hosted mode only on that path. Runic defines no well-known flag and no environment variable
/// for it, so an application without that code path cannot be hosted:
/// </para>
/// <code>
/// if (args is ["hosted"])
/// {
///     try
///     {
///         return RunicDesktopHost.Run(provider, new DesktopHostOptions(),
///             new RunicDesktopHostOptions { Guest = RunicGuestOptions.FromStandardInput() },
///             async host =&gt;
///             {
///                 // When the host goes away while the Window opens, the open throws; Run then returns 0.
///                 await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
///                 await window.WaitForCloseAsync();
///                 return 0;
///             });
///     }
///     catch (RunicHostedApplicationException) { return 1; } // Not started by a host; the reason is on standard error.
/// }
/// </code>
/// <para>
/// In hosted mode <c>Run</c> starts no window toolkit loop and never shows a window of its own. Before the body runs, it
/// reads the host's offer from standard input, opens the host channel on a loopback listener of its own, writes its
/// address and key to standard output and waits for the host to connect and prove itself; a failure throws
/// <see cref="RunicHostedApplicationException"/> without running the body. Each Window then opens in a window of the
/// host: its page is served as in serve-only mode, the host's window loads it with a one-time launch address, and the
/// Window's <see cref="IRunicWindowPresentation"/> and close go through the host. Platform services that need the native
/// window, such as file dialogs, report <c>OwnerUnavailable</c>, as in serve-only mode.
/// </para>
/// <para>
/// When the host goes away (standard input ends or the channel closes) or asks to shut down, the Windows close,
/// <see cref="Runic.Desktop.DesktopHost.ShutdownRequested"/> is cancelled and <c>Run</c> returns when the body does; a body
/// that has not returned 10 seconds later ends the process. A Window that is opening or opens after the host went away
/// fails with a <see cref="RunicWindowConfigurationException"/> of code <c>hosted-host-gone</c>; when the body ends with
/// it, or with an <see cref="OperationCanceledException"/>, after the host went away, <c>Run</c> returns 0, since the host
/// going away is the normal end of a hosted application. A request to the host that has had no reply within 30
/// seconds reports a failure. Recent documents report <c>BackendUnavailable</c> in hosted mode, as in serve-only mode. A
/// hosted application does not claim <c>ApplicationInstance</c>: each host session has a guest process of its own.
/// </para>
/// <para>
/// Any process of the same user that knows the parameter can host the application; <see cref="AcceptHost"/> is a product
/// filter, not a security boundary. Hosted mode refuses to start when the process runs as another user than its parent,
/// or cannot show that it does (on Linux and macOS its real and effective user ids, on Windows its token's user). It
/// also refuses when the process runs elevated, unless <see cref="AllowElevatedHost"/> and the host's
/// <see cref="RunicGuestStartInfo.AllowElevatedGuest"/> allow the one elevated pair: a parent that runs elevated as the
/// same user. The protocol is experimental; see <c>docs/experimental.md</c>.
/// </para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicGuestOptions
{
    private readonly Func<Stream> _input;
    private readonly Func<TextWriter> _output;

    private RunicGuestOptions(Func<Stream> input, Func<TextWriter> output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Hosted mode over the process's standard input and standard output, which the host piped when it started the
    /// application.
    /// </summary>
    /// <returns>The options; <c>Run</c> reads standard input only when it starts.</returns>
    public static RunicGuestOptions FromStandardInput() => new(Console.OpenStandardInput, () => Console.Out);

    // Tests and the conformance runner give the guest its own pipes.
    internal static RunicGuestOptions FromStreams(Stream input, TextWriter output) => new(() => input, () => output);

    internal Stream OpenInput() => _input();

    internal TextWriter Output => _output();

    /// <summary>
    /// Decides whether to be hosted by the host the offer names; <see langword="null"/>, the default, accepts every host.
    /// A host it declines gets the refusal <c>host-not-accepted</c>, and <c>Run</c> throws.
    /// </summary>
    /// <remarks>The name and version are what the host says about itself: a filter for the product, not a credential.</remarks>
    public Func<RunicHostInfo, bool>? AcceptHost { get; init; }

    /// <summary>
    /// Whether the application, when it runs elevated, accepts a host that runs elevated as the same user;
    /// <see langword="false"/>, the default, refuses hosted mode whenever the process runs elevated (<c>guest-elevated</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For administrative tools whose host and guest both need administrator rights, such as an installer that hosts a
    /// check tool. It has no effect on a process that does not run elevated. With <see langword="true"/>, a process that
    /// runs elevated (an elevated token on Windows, an effective user id 0 on Linux and macOS) is hosted only when all of
    /// these hold, and refuses otherwise:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The host's offer allows an elevated guest (<see cref="RunicGuestStartInfo.AllowElevatedGuest"/>); otherwise <c>guest-elevated</c>.</description></item>
    /// <item><description>Its parent process is the process that started it, not a newer one that reused a gone parent's id, and runs as the same user (the token's user on Windows; the real and effective user ids on Linux and macOS); otherwise <c>guest-other-user</c>.</description></item>
    /// <item><description>That parent runs elevated (Windows: its token is elevated, or its integrity level is High or above; Linux and macOS: its effective user id is 0); otherwise <c>guest-elevated-host-not-elevated</c>.</description></item>
    /// </list>
    /// <para>
    /// The application checks its parent's token or user ids itself; nothing the host says counts as evidence. Processes
    /// of the same user that do not run elevated cannot read the channel's secrets or the Windows' launch addresses, but
    /// they can still connect to its loopback listeners, see the screen and read the clipboard; an elevating launcher that
    /// relays another process's standard input and output, such as <c>sudo</c>, makes that process the host. Start the
    /// application only from a location that only administrators can change. The residual risks are in
    /// <c>docs/security.md</c>, "Hosted application windows".
    /// </para>
    /// </remarks>
    public bool AllowElevatedHost { get; init; }

    /// <summary>
    /// How long the hosted start may take, from <c>Run</c> to the host's authenticated <c>hello</c>: 30 seconds by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan StartTimeout
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = Hosting.HostProtocol.DefaultStartTimeout;

    /// <summary>The application's name the host sees; <see langword="null"/>, the default, is the entry assembly's name.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    /// The application's version the host sees; <see langword="null"/>, the default, is the entry assembly's
    /// informational version without build metadata.
    /// </summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>
    /// The application's identity the host may key what it keeps about it by, such as saved window placements, in
    /// reverse-DNS form like <c>com.example.tool</c>; <see langword="null"/>, the default, tells the host none.
    /// </summary>
    public string? ApplicationId { get; init; }

    // How long a connection may take to upgrade, and then to send its first message (host-protocol.md, Channel listener).
    internal TimeSpan UnauthenticatedTimeout { get; init; } = Hosting.HostProtocol.UnauthenticatedTimeout;

    internal TimeSpan HelloTimeout { get; init; } = Hosting.HostProtocol.HelloTimeout;

    // How long the guest waits for the reply to one of its requests, and for the host to take one message
    // (host-protocol.md, Messages).
    internal TimeSpan RequestTimeout { get; init; } = Hosting.HostProtocol.RequestTimeout;

    internal TimeSpan SendTimeout { get; init; } = Hosting.HostProtocol.SendTimeout;

    // The host's requests the guest handles at once.
    internal int MaximumWaitingRequests { get; init; } = Hosting.HostProtocol.MaximumWaitingRequests;

    // How long the body may run after the host went away before the process exits.
    internal TimeSpan HostGoneExitTimeout { get; init; } = Hosting.HostProtocol.HostGoneExitTimeout;

    // Ends the process; tests replace it.
    internal Action<int> ExitProcess { get; init; } = Environment.Exit;

    // This process's privileges and its parent's, read once as hosted mode starts; tests replace it to play an elevated
    // process, parent or both.
    internal Func<Hosting.GuestPrivilegeFacts> ReadPrivileges { get; init; } = Hosting.GuestPrivileges.Read;

    // Tests only: elevated-peer admission's check of a connection's peer (the peer's end, the listener's end), and
    // whether the system has one at all; an elevated pair applies them (host-protocol.md, Elevated-peer admission).
    internal Func<System.Net.IPEndPoint, System.Net.IPEndPoint, bool> CheckPeer { get; init; } = Runic.Desktop.Internal.ElevatedPeers.IsElevated;

    internal bool CanCheckPeers { get; init; } = Runic.Desktop.Internal.ElevatedPeers.IsAvailable;

    internal string ResolvedName =>
        ApplicationName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName;

    internal string ResolvedVersion
    {
        get
        {
            if (ApplicationVersion is not null) return ApplicationVersion;
            var entry = Assembly.GetEntryAssembly();
            var version = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? entry?.GetName().Version?.ToString() ?? "0.0.0";
            var metadata = version.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? version : version[..metadata];
        }
    }
}

/// <summary>The host application that shows a guest's windows, as its offer names it.</summary>
/// <param name="Name">The host application's name.</param>
/// <param name="Version">The host application's version.</param>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostInfo(string Name, string Version);

/// <summary>A running hosted mode: the host channel of <see cref="RunicDesktopHost.Guest"/>.</summary>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicGuestSession
{
    internal RunicGuestSession(RunicHostInfo host, int protocol, IReadOnlyList<string> features, CancellationToken hostGone)
    {
        Host = host;
        Protocol = protocol;
        Features = features;
        HostGone = hostGone;
    }

    /// <summary>The host that shows this application's windows.</summary>
    public RunicHostInfo Host { get; }

    /// <summary>The version of the Runic host protocol in use: 1.</summary>
    public int Protocol { get; }

    /// <summary>The protocol's features in use on the channel, such as <c>dialogs/1</c>; empty for the base protocol.</summary>
    public IReadOnlyList<string> Features { get; }

    /// <summary>Cancelled when the host went away or asked the application to shut down.</summary>
    public CancellationToken HostGone { get; }

    /// <summary>
    /// The host's application locale, a BCP 47 tag such as <c>de-CH</c>, or <see langword="null"/> while the host sets
    /// none. The application's <see cref="IRunicApplicationLocale"/> follows it unless the application sets a locale of
    /// its own.
    /// </summary>
    /// <remarks>It changes when the host's locale does, before the application's locale follows.</remarks>
    public string? Locale => Volatile.Read(ref _state).Locale;

    /// <summary>
    /// The languages the host's user prefers, most preferred first, at most 32: for an application that chooses its own
    /// locale, such as <c>catalog.ResolveLocale(session.Languages)</c>, where it would read <c>DesktopLanguages.Preferred</c>.
    /// </summary>
    public IReadOnlyList<string> Languages => Volatile.Read(ref _state).Languages;

    /// <summary>
    /// The host's appearance, which the application's Windows show (their pages' appearance and their
    /// <c>prefers-color-scheme</c>), instead of this process's own desktop settings; <see langword="null"/> while the host
    /// reports none.
    /// </summary>
    public Runic.Platform.DesktopAppearance? Appearance => Volatile.Read(ref _state).Appearance;

    private State _state = new(null, null, []);

    private sealed record State(Runic.Platform.DesktopAppearance? Appearance, string? Locale, IReadOnlyList<string> Languages);

    internal void Update(Runic.Platform.DesktopAppearance? appearance, string? locale, IReadOnlyList<string> languages) =>
        Volatile.Write(ref _state, new State(appearance, locale, languages));
}
