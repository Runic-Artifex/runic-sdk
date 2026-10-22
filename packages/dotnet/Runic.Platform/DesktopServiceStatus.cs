namespace Runic.Platform;

/// <summary>
/// An application-scoped service (<see cref="IDesktopSettings"/>, <see cref="IDesktopNotifications"/>,
/// <see cref="IDesktopSecretStore"/> and the other <c>IDesktop…</c> services) that reports whether it can be used at all.
/// </summary>
/// <remarks>
/// Read it through <see cref="DesktopServiceStatus.Get"/>, which also answers for services that do not implement this
/// interface. Window-scoped services report their capabilities through <see cref="IPlatformCapabilities"/> instead.
/// </remarks>
public interface IDesktopServiceStatus
{
    /// <summary>
    /// <see cref="CapabilityStatus.Unavailable"/> when every operation reports <see cref="PlatformResult{T}.Unavailable"/>
    /// with that reason, for example a service an <c>Unavailable(…)</c> factory created because no provider supports
    /// this platform or host; otherwise <see cref="CapabilityStatus.Available"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="CapabilityStatus.Available"/> means the service was created for a native backend, not that each
    /// request succeeds: a request still reports the backend's outcome, including
    /// <see cref="PlatformResult{T}.Unavailable"/> when a desktop service turns out to be missing. Use it to decide
    /// whether to offer a feature, and the request's result to report what happened.
    /// </remarks>
    CapabilityStatus Status { get; }
}

/// <summary>Reads the status of application-scoped services.</summary>
public static class DesktopServiceStatus
{
    private static readonly CapabilityStatus Available = new CapabilityStatus.Available();

    /// <summary>Gets whether <paramref name="service"/> can be used at all.</summary>
    /// <param name="service">An application-scoped service, such as the result of a <c>Create…</c> factory.</param>
    /// <returns>The service's <see cref="IDesktopServiceStatus.Status"/>, or <see cref="CapabilityStatus.Available"/>
    /// for a service that does not report one.</returns>
    /// <example><code>
    /// var secrets = DesktopPlatformServices.CreateSecretStore("org.example.App");
    /// bool offerRememberMe = DesktopServiceStatus.Get(secrets) is CapabilityStatus.Available;
    /// </code></example>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public static CapabilityStatus Get(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is IDesktopServiceStatus reporting ? reporting.Status ?? Available : Available;
    }

    internal static CapabilityStatus Unavailable(PlatformUnavailableReason reason) => new CapabilityStatus.Unavailable(reason);
}
