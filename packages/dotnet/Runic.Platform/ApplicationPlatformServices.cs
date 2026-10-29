using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Native services of the application that need no window: opening URIs and files, and revealing files.</summary>
/// <remarks>
/// <para>
/// A host registers one instance for the application, so a service without a window of its own, such as a link handler,
/// a menu command shared by several windows or a background download, takes this interface in its constructor. It works
/// before the first window opens, while every window is closed, for an application behind a status icon and for Windows
/// served to a browser.
/// </para>
/// <para>
/// Prefer a window's <see cref="IWindowPlatformServices.FileLauncher"/> while that window is shown: the desktop can then
/// place an application chooser over it. The application's launcher reports
/// <see cref="PlatformUnavailableReason.OwnerUnavailable"/> only where a platform needs a parent window, which none of
/// Runic's providers does.
/// </para>
/// </remarks>
public interface IApplicationPlatformServices : IPlatformCapabilities
{
    /// <summary>Hands local files and web, mail or Windows Settings URIs to native applications, without a window.</summary>
    /// <remarks>
    /// The rules and results of <see cref="IDesktopFileLauncher"/>: the same URI policy, the same refusal to open programs,
    /// and <see cref="DesktopFileOperation.Reveal"/> and <see cref="DesktopFileOperation.ChooseApplication"/>, which shows
    /// its chooser without a parent. Its capabilities are <see cref="PlatformCapabilityIds.FilesLaunch"/> and
    /// <see cref="PlatformCapabilityIds.UrisLaunch"/>.
    /// </remarks>
    IDesktopFileLauncher FileLauncher { get; }
}

/// <summary>Application platform services for hosts that cannot provide them.</summary>
public static class ApplicationPlatformServices
{
    /// <summary>The capability identifiers the application's platform services report.</summary>
    public static ImmutableArray<string> CapabilityIds { get; } = [PlatformCapabilityIds.FilesLaunch, PlatformCapabilityIds.UrisLaunch];

    /// <summary>Creates services that report every operation and capability unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for invalid arguments and caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IApplicationPlatformServices Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableApplicationPlatformServices(reason);
    }

    private sealed class UnavailableApplicationPlatformServices(PlatformUnavailableReason reason) : IApplicationPlatformServices
    {
        private readonly IWindowPlatformServices _window = WindowPlatformServices.Unavailable(reason);
        private readonly CapabilitySnapshot _snapshot = new(Guid.Empty,
            CapabilityIds.ToImmutableDictionary(static id => id, CapabilityStatus (_) => new CapabilityStatus.Unavailable(reason)));

        public IDesktopFileLauncher FileLauncher => _window.FileLauncher;
        public CapabilitySnapshot GetSnapshot() => _snapshot;
    }
}
