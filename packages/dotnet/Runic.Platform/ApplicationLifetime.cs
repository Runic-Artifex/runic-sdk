using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>One launch of the application: its arguments, working directory and desktop activation data.</summary>
/// <remarks>Treat a forwarded launch as untrusted input at the application boundary, like command-line arguments.</remarks>
public sealed record ApplicationLaunch(ImmutableArray<string> Arguments, string WorkingDirectory)
{
    /// <summary>Short-lived desktop activation data for raising a window. Dispatch promptly; do not persist it.</summary>
    public DesktopActivationContext? PlatformContext { get; init; }

    /// <summary>
    /// The way back to the launching process, on a launch the running instance received from a process that waits
    /// for its reply; null for a launch that only hands over its arguments. Complete it once the launch is handled.
    /// </summary>
    /// <remarks>Not sent when forwarding a launch: the running instance sets it.</remarks>
    public IApplicationLaunchReply? Reply { get; init; }

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
    /// A launch from a process that waits for a reply carries <see cref="ApplicationLaunch.Reply"/>; complete it once
    /// the launch is handled, outside the handler if the work takes longer.
    /// </remarks>
    event EventHandler<ApplicationLaunch>? Launched;
}

/// <summary>The outcome of claiming the application's single instance.</summary>
/// <remarks>This set is closed: it gains cases only in a major release.</remarks>
public abstract record ApplicationInstanceClaim
{
    private ApplicationInstanceClaim() { }

    /// <summary>This process is the running instance. Keep the instance until the application ends.</summary>
    public sealed record Primary(IApplicationInstance Instance) : ApplicationInstanceClaim;

    /// <summary>Another process is the running instance and received this launch. This process should exit.</summary>
    public sealed record Forwarded : ApplicationInstanceClaim
    {
        /// <summary>
        /// The running instance's reply when this process asked to wait for one: its messages and exit status. Null
        /// when the launch was only handed over. Dispose it before exiting.
        /// </summary>
        public IForwardedLaunchReply? Reply { get; init; }
    }

    /// <summary>
    /// No instance runs, and this process did not become one, because it claimed with <c>ForwardOnly</c>. The launch
    /// was not handled: handle it here, or report that the application is not running.
    /// </summary>
    public sealed record NotRunning : ApplicationInstanceClaim;
}

/// <summary>
/// The running instance's side of a reply to a forwarded launch: messages to the launching process, then its exit
/// status. One launching process, for example a command line, reads them while the running instance does the work.
/// </summary>
/// <remarks>
/// Members can be called from any thread; writes are sent one at a time, in order. A write waits while the launching
/// process does not read, so a slow reader slows the writer instead of filling memory.
/// </remarks>
public interface IApplicationLaunchReply
{
    /// <summary>
    /// Cancelled when the launching process asks to cancel (for example on Ctrl+C), when it goes away, or when this
    /// instance ends: <see cref="CancelRequested"/> or <see cref="Disconnected"/>, which is cancelled first, so a
    /// callback registered on this token can tell which. After a cancel request the launching process still reads
    /// messages until the reply completes.
    /// </summary>
    CancellationToken Cancelled { get; }

    /// <summary>
    /// Cancelled when the launching process asks to cancel with <see cref="IForwardedLaunchReply.CancelAsync"/>, for
    /// example on Ctrl+C. It keeps reading messages until the reply completes.
    /// </summary>
    /// <remarks>
    /// Pass this token rather than <see cref="Cancelled"/> to work that should outlive the launching process, such as
    /// an operation a command line started over SSH: it is not cancelled when the connection drops. The default
    /// implementation returns <see cref="Cancelled"/>, for a reply that cannot tell the two apart; the replies Runic
    /// creates implement it.
    /// </remarks>
    CancellationToken CancelRequested => Cancelled;

    /// <summary>
    /// Cancelled when the reply ends without an exit status: the launching process went away or closed it (disposing
    /// its reply, or cancelling a read), this instance ended, or a write here was cancelled. Writes then return false.
    /// </summary>
    /// <remarks>
    /// The default implementation returns <see cref="Cancelled"/>, for a reply that cannot tell the two apart; the
    /// replies Runic creates implement it.
    /// </remarks>
    CancellationToken Disconnected => Cancelled;

    /// <summary>Sends one message, such as an output line or a JSON event, as UTF-8.</summary>
    /// <param name="message">At most 1 MiB in UTF-8.</param>
    /// <param name="cancellationToken">
    /// Stops waiting for a launching process that does not read. A write cancelled this way ends the reply, since the
    /// launching process may have received only part of the message.
    /// </param>
    /// <returns>False, without sending, once the launching process went away or this instance ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="message"/> is larger than 1 MiB in UTF-8.</exception>
    /// <exception cref="InvalidOperationException">The reply was completed.</exception>
    ValueTask<bool> WriteAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <paramref name="exitCode"/> after the messages written before and ends the reply. The launching process
    /// waits until it is called, or until this instance ends.
    /// </summary>
    /// <param name="exitCode">The launching process's exit status, for example 0, or 130 after a cancel request.</param>
    /// <param name="cancellationToken">Stops waiting for a launching process that does not read, ending the reply.</param>
    /// <exception cref="InvalidOperationException">The reply was already completed.</exception>
    ValueTask CompleteAsync(int exitCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// The launching process's side of a reply to a forwarded launch: the running instance's messages, in order, then its
/// exit status. Disposing it before the exit status arrives cancels the launch in the running instance.
/// </summary>
/// <remarks>Read from one caller at a time. <see cref="CancelAsync"/> can be called at any time, from any thread.</remarks>
public interface IForwardedLaunchReply : IAsyncDisposable
{
    /// <summary>
    /// The messages not read yet. The sequence ends at the exit status, or when the running instance ended without one;
    /// <see cref="WaitForExitAsync"/> then tells which.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops reading and closes the connection, which cancels the launch in the running instance.
    /// </param>
    IAsyncEnumerable<string> ReadMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for the exit status, discarding messages not read yet.</summary>
    /// <param name="cancellationToken">
    /// Stops waiting and closes the connection, which cancels the launch in the running instance.
    /// </param>
    /// <returns>
    /// The exit status; <see cref="PlatformFailureCode.IOError"/> when the running instance ended or the connection
    /// broke before it answered, and <see cref="PlatformFailureCode.InvalidData"/> for a malformed reply.
    /// </returns>
    ValueTask<PlatformResult<int>> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the running instance to cancel the launch, cancelling <see cref="IApplicationLaunchReply.CancelRequested"/>
    /// (and with it <see cref="IApplicationLaunchReply.Cancelled"/>), and keeps
    /// the connection: the running instance's last messages and exit status still arrive. Later calls do nothing.
    /// </summary>
    ValueTask CancelAsync();
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
