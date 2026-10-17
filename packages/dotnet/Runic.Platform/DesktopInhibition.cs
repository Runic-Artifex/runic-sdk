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

/// <summary>Creates idle-power inhibition for a host or platform without a provider.</summary>
public static class DesktopInhibition
{
    /// <summary>Creates inhibition that supports no effects and reports every acquisition unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// Acquisition validates its arguments as a provider does, so a call that is wrong on one platform is wrong on every
    /// platform; otherwise nothing throws except caller cancellation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopInhibition Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableInhibition(reason);
    }

    private sealed class UnavailableInhibition(PlatformUnavailableReason unavailable) : IDesktopInhibition
    {
        public DesktopInhibitionEffects SupportedEffects => DesktopInhibitionEffects.None;

        public ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason,
            CancellationToken cancellationToken = default)
        {
            // The rules of InhibitionValidation in Runic.Platform.Runtime, which references this package.
            if (effects == DesktopInhibitionEffects.None || (effects & ~(DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep)) != 0)
                throw new ArgumentOutOfRangeException(nameof(effects));
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (reason.Length > 128 || reason.Contains('\0')) throw new ArgumentException("Use a reason of at most 128 characters without NUL.", nameof(reason));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Unavailable(unavailable));
        }
    }
}
