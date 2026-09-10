namespace Runic.Platform;

/// <summary>Idle power transitions an explicit operation may ask the desktop to inhibit.</summary>
[Flags]
public enum DesktopInhibitionEffects
{
    /// <summary>No request.</summary>
    None = 0,
    /// <summary>Ask the system not to sleep due to inactivity.</summary>
    SystemSleep = 1,
    /// <summary>Ask the display not to sleep due to inactivity. May also inhibit idle system sleep.</summary>
    DisplaySleep = 2,
}

/// <summary>An operation-owned native inhibition request. Dispose as soon as the operation ends.</summary>
/// <remarks>Holding this request does not override user actions, battery/thermal policy or forced shutdown.</remarks>
public interface IDesktopInhibitionLease : IAsyncDisposable
{
    /// <summary>The requested effects, not a guarantee that desktop policy honors them.</summary>
    DesktopInhibitionEffects Effects { get; }
}

/// <summary>Requests temporary idle-power inhibition; never enabled implicitly by application startup.</summary>
public interface IDesktopInhibition
{
    /// <summary>Effects implemented by this provider. Availability and permission are checked on acquisition.</summary>
    DesktopInhibitionEffects SupportedEffects { get; }
    /// <summary>Acquires an independent lease with a short, localized, user-visible reason.</summary>
    /// <remarks>Cancellation applies to acquisition. Use await using around an operation that observes its own cancellation token.</remarks>
    ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason,
        CancellationToken cancellationToken = default);
}
