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
/// that has not returned 10 seconds later ends the process. A hosted application does not claim
/// <c>ApplicationInstance</c>: each host session has a guest process of its own.
/// </para>
/// <para>
/// Any process of the same user that knows the parameter can host the application; <see cref="AcceptHost"/> is a product
/// filter, not a security boundary. Hosted mode refuses to start when the process runs elevated, or (on Linux) as another
/// user than its parent. The protocol is experimental; see <c>docs/experimental.md</c>.
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

    // How long a connection may take to upgrade and authenticate (host-protocol.md, Channel listener).
    internal TimeSpan UnauthenticatedTimeout { get; init; } = Hosting.HostProtocol.UnauthenticatedTimeout;

    // How long the body may run after the host went away before the process exits.
    internal TimeSpan HostGoneExitTimeout { get; init; } = Hosting.HostProtocol.HostGoneExitTimeout;

    // Ends the process; tests replace it.
    internal Action<int> ExitProcess { get; init; } = Environment.Exit;

    // The refusal code for this process's privileges, or null; tests replace it.
    internal Func<string?> CheckPrivileges { get; init; } = Hosting.GuestPrivileges.Check;

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
}
