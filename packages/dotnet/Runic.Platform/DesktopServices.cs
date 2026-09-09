using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>The desktop's preference; an application override takes precedence.</summary>
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
    public Uri? ActivationUri { get; init; }
}
/// <summary>A user action on a notification. Treat activation as untrusted input at the application boundary.</summary>
public sealed record DesktopNotificationActivation(string NotificationId, string ActionId, Uri? ActivationUri = null);
/// <summary>Application-scoped native notifications with explicit authorization.</summary>
public interface IDesktopNotifications : IAsyncDisposable
{
    /// <summary>Raised on a worker thread; dispatch window work through its presentation.</summary>
    event EventHandler<DesktopNotificationActivation>? Activated;
    /// <summary>Requests or checks OS authorization; Windows and Linux do not show a separate consent prompt.</summary>
    /// <remarks>Windows may not have settings for a newly registered app. Success permits an attempt;
    /// submission still enforces native registration and policy and never guarantees visibility.</remarks>
    ValueTask<PlatformResult<Unit>> RequestPermissionAsync(CancellationToken cancellationToken = default);
    /// <summary>Submits or replaces a notification; success acknowledges submission, not visibility.</summary>
    ValueTask<PlatformResult<Unit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default);
    /// <summary>Removes a notification with an application-owned identifier.</summary>
    ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default);
}
/// <summary>Local-file handoff operations; asking for an application is distinct from opening the default.</summary>
public enum DesktopFileOperation
{
    /// <summary>Open with the registered default application.</summary>
    Open,
    /// <summary>Ask the user which application should open the file.</summary>
    ChooseApplication,
    /// <summary>Show the containing directory, selecting the file where supported.</summary>
    Reveal
}
/// <summary>Native file handoff. Paths are supplied by trusted C# application code, never directly by a web client.</summary>
public interface IDesktopFileLauncher
{
    /// <summary>The caller must retain any sandbox/security-scoped access until the operation completes.</summary>
    /// <remarks>Success acknowledges native handling, not that another application opened the file.
    /// Windows Open With can report success even when dismissed; UserDismissed is returned only
    /// when the native API distinguishes dismissal.</remarks>
    ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
        CancellationToken cancellationToken = default);
}

/// <summary>An acquired file that can be handed to a native application without exposing its path.</summary>
public interface ILaunchableFileLease : IAsyncDisposable
{
    /// <summary>Retains the acquired access grant until native handoff completes, including racing disposal.</summary>
    ValueTask<PlatformResult<Unit>> LaunchAsync(IDesktopFileLauncher launcher,
        DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default);
}
