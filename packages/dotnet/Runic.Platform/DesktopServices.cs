using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>The desktop's preference; an application override takes precedence.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum DesktopColorScheme
{
    /// <summary>No native preference is available.</summary>
    NoPreference,
    /// <summary>Light appearance.</summary>
    Light,
    /// <summary>Dark appearance.</summary>
    Dark
}
/// <summary>An sRGB colour with components in [0,1].</summary>
public sealed record DesktopAccentColor(double Red, double Green, double Blue);
/// <summary>Unknown optional preferences remain null rather than becoming false.</summary>
public sealed record DesktopAppearance(DesktopColorScheme ColorScheme = DesktopColorScheme.NoPreference,
    DesktopAccentColor? AccentColor = null, bool? HighContrast = null, bool? ReducedMotion = null);
/// <summary>Application-scoped desktop preferences. Dispose to stop observation.</summary>
public interface IDesktopSettings : IAsyncDisposable
{
    /// <summary>Reads the current native preferences.</summary>
    ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>Emits an initial value and subsequent changes, including unavailability/recovery.</summary>
    IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync(CancellationToken cancellationToken = default);
}
/// <summary>A notification button. IDs are application-owned routing keys, never commands.</summary>
public sealed record DesktopNotificationAction(string Id, string Label);
/// <summary>Reusing Id replaces a notification. Submission does not guarantee display.</summary>
public sealed record DesktopNotification(string Id, string Title, string Body)
{
    /// <summary>Up to four actions with distinct application-owned identifiers.</summary>
    public ImmutableArray<DesktopNotificationAction> Actions { get; init; } = [];
    /// <summary>Optional registered application URI for activation after process exit.</summary>
    /// <remarks>On Windows the shell launches this URI, with notification and action query parameters,
    /// through its registered protocol handler instead of raising Activated, including while the process runs.</remarks>
    public Uri? ActivationUri { get; init; }
}
/// <summary>A user action on a notification. Treat activation as untrusted input at the application boundary.</summary>
public sealed record DesktopNotificationActivation(string NotificationId, string ActionId, Uri? ActivationUri = null)
{
    /// <summary>Short-lived desktop activation data. Dispatch promptly; do not persist it.</summary>
    public DesktopActivationContext? PlatformContext { get; init; }
}

/// <summary>Opaque, short-lived focus authorization supplied by the desktop.</summary>
public sealed class DesktopActivationContext
{
    /// <summary>Wayland activation token, when supplied by the compositor.</summary>
    public string? ActivationToken { get; }
    /// <summary>X11 startup-notification identifier, when supplied by the desktop.</summary>
    public string? StartupId { get; }
    /// <summary>Creates context for immediate presentation dispatch.</summary>
    public DesktopActivationContext(string? activationToken = null, string? startupId = null)
    {
        Validate(activationToken); Validate(startupId);
        ActivationToken = activationToken; StartupId = startupId;
    }
    private static void Validate(string? value)
    {
        if (value is not null && (value.Length > 4096 || value.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException("Invalid desktop activation context.");
    }
    /// <inheritdoc />
    public override string ToString() => "DesktopActivationContext { opaque }";
}
/// <summary>Application-scoped native notifications with explicit authorization.</summary>
public interface IDesktopNotifications : IAsyncDisposable
{
    /// <summary>Raised on a worker thread; dispatch window work through its presentation.</summary>
    event EventHandler<DesktopNotificationActivation>? Activated;
    /// <summary>Requests or checks OS authorization; Windows and Linux do not show a separate consent prompt.</summary>
    /// <remarks>Windows may not have settings for a newly registered app. Success permits an attempt;
    /// submission still enforces native registration and policy and never guarantees visibility.</remarks>
    ValueTask<PlatformResult<PlatformUnit>> RequestPermissionAsync(CancellationToken cancellationToken = default);
    /// <summary>Submits or replaces a notification; success acknowledges submission, not visibility.</summary>
    ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default);
    /// <summary>Removes a notification with an application-owned identifier.</summary>
    ValueTask<PlatformResult<PlatformUnit>> RemoveAsync(string id, CancellationToken cancellationToken = default);
}
/// <summary>Local-file handoff operations; asking for an application is distinct from opening the default.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum DesktopFileOperation
{
    /// <summary>Open with the registered default application.</summary>
    Open,
    /// <summary>Ask the user which application should open the file.</summary>
    ChooseApplication,
    /// <summary>Show the containing directory, selecting the file where supported.</summary>
    Reveal
}
/// <summary>Native file and URI handoff. Paths and URIs are supplied by trusted C# application code, never directly by a web client.</summary>
public interface IDesktopFileLauncher
{
    /// <summary>The caller must retain any sandbox/security-scoped access until the operation completes.</summary>
    /// <remarks>Success acknowledges native handling, not that another application opened the file.
    /// Windows Open With can report success even when dismissed; UserDismissed is returned only
    /// when the native API distinguishes dismissal. Windows and macOS Open returns PermissionDenied
    /// for programs, scripts, installers and shortcuts instead of running them; Linux applies the
    /// desktop portal's policy. The Windows and macOS block lists are best effort: do not Open files
    /// from untrusted sources, such as downloads or names a page supplies; Reveal them or let the user
    /// ChooseApplication instead. Windows rejects UNC, device and alternate-stream paths.</remarks>
    ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
        CancellationToken cancellationToken = default);

    /// <summary>Opens an absolute <c>http</c>, <c>https</c>, <c>mailto</c> or <c>ms-settings</c> URI in the user's default
    /// handler, such as the default browser, the mail client or, for <c>ms-settings</c>, the Windows Settings app.</summary>
    /// <param name="uri">The URI. Relative URIs, paths (an absolute path is a <c>file:</c> URI) and every other scheme are
    /// rejected before anything native runs, so a launch never executes a program; open local files with
    /// <see cref="LaunchAsync"/>.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the desktop.</param>
    /// <remarks>Success acknowledges that the desktop accepted the URI, not that an application showed it. Linux asks the
    /// desktop portal (OpenURI), which may let the user choose an application and reports
    /// <see cref="PlatformFailureCode.UserDismissed"/> when they cancel; Windows uses the shell's URL association; macOS
    /// uses <c>NSWorkspace</c>. Without a registered handler, for example no mail client for <c>mailto</c>, the desktop
    /// may offer to find an application (Windows 11 does, and reports success), or the result is
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> or a failure, depending on the platform. An
    /// <c>ms-settings</c> URI must be exactly <c>ms-settings:</c> and a page of 1 to 64 letters, digits or hyphens, such as
    /// <c>ms-settings:display</c>, with no path, query, fragment or escapes; pages ending in <c>-action</c>, which act
    /// instead of showing a page, are refused. Only Windows opens it; Linux and macOS validate it the same way and return
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> without a native call.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is relative, a path, not an http, https, mailto or
    /// ms-settings URI, or an ms-settings URI outside the page rule.</exception>
    ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>An acquired file that can be handed to a native application without exposing its path.</summary>
public interface ILaunchableFileLease : IAsyncDisposable
{
    /// <summary>Retains the acquired access grant until native handoff completes, including racing disposal.</summary>
    ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(IDesktopFileLauncher launcher,
        DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default);
}
