using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>One launch of the application: its arguments, working directory and desktop activation data.</summary>
/// <remarks>Treat a forwarded launch as untrusted input at the application boundary, like command-line arguments.</remarks>
public sealed record ApplicationLaunch(ImmutableArray<string> Arguments, string WorkingDirectory)
{
    /// <summary>Short-lived desktop activation data for raising a window. Dispatch promptly; do not persist it.</summary>
    public DesktopActivationContext? PlatformContext { get; init; }

    /// <summary>
    /// This process's launch: <paramref name="arguments"/>, the current directory, and the activation token or
    /// startup ID the desktop passed in <c>XDG_ACTIVATION_TOKEN</c> or <c>DESKTOP_STARTUP_ID</c>.
    /// </summary>
    public static ApplicationLaunch FromProcess(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? token = Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN");
        string? startupId = Environment.GetEnvironmentVariable("DESKTOP_STARTUP_ID");
        DesktopActivationContext? context = null;
        if (!string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(startupId))
        {
            try { context = new DesktopActivationContext(NullIfEmpty(token), NullIfEmpty(startupId)); }
            catch (ArgumentException) { /* Unusable activation data cannot raise a window; launch without it. */ }
        }
        return new ApplicationLaunch([.. arguments], Environment.CurrentDirectory) { PlatformContext = context };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>The running instance of an application, which receives the launches of later processes.</summary>
public interface IApplicationInstance : IAsyncDisposable
{
    /// <summary>
    /// Raised for each later launch, on a worker thread, one at a time and in order; dispatch window work through the
    /// application's host.
    /// </summary>
    /// <remarks>
    /// Launches wait for the first handler, so subscribe right after claiming, before slow start-up work. A launching
    /// process reports itself forwarded only once a handler received its launch; one that is not received within a
    /// few seconds, or arrives while this instance is ending, keeps trying and may become the next running instance.
    /// </remarks>
    event EventHandler<ApplicationLaunch>? Launched;
}

/// <summary>The outcome of claiming the application's single instance.</summary>
public abstract record ApplicationInstanceClaim
{
    private ApplicationInstanceClaim() { }

    /// <summary>This process is the running instance. Keep the instance until the application ends.</summary>
    public sealed record Primary(IApplicationInstance Instance) : ApplicationInstanceClaim;

    /// <summary>Another process is the running instance and received this launch. This process should exit.</summary>
    public sealed record Forwarded : ApplicationInstanceClaim;
}

/// <summary>What to start when the user logs in.</summary>
/// <param name="Arguments">Arguments for the application, for example one that starts it without a window.</param>
public sealed record LoginStartupRequest(ImmutableArray<string> Arguments)
{
    /// <summary>Why the application starts at login, shown where the desktop asks the user.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Starting the application when the user logs in. Nothing is registered until <see cref="EnableAsync"/> is called,
/// which an application does only when the user asks for it.
/// </summary>
public interface IDesktopLoginStartup
{
    /// <summary>Whether the application starts at login.</summary>
    /// <remarks>Unavailable where the platform cannot tell, such as a Flatpak or Snap sandbox on Linux.</remarks>
    ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the application at login with <paramref name="request"/>, replacing an earlier registration.</summary>
    /// <remarks>
    /// <see cref="PlatformFailureCode.PermissionDenied"/> when the user or the desktop refused, including a startup
    /// entry the user turned off in the desktop's own settings, which the application does not override.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops starting the application at login and removes what <see cref="EnableAsync"/> registered.</summary>
    /// <remarks>It also forgets that the user turned the entry off, so a later <see cref="EnableAsync"/> registers it again.</remarks>
    ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default);
}

/// <summary>Login startup for hosts and platforms that cannot provide it.</summary>
public static class DesktopLoginStartup
{
    /// <summary>Creates login startup that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for a missing argument and caller cancellation.</remarks>
    public static IDesktopLoginStartup Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableLoginStartup(reason);
    }

    private sealed class UnavailableLoginStartup(PlatformUnavailableReason reason) : IDesktopLoginStartup
    {
        public ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Answer(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default) => Answer(cancellationToken);

        private ValueTask<PlatformResult<PlatformUnit>> Answer(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }
    }
}
