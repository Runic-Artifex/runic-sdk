using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Native services of the application that need no window: opening URIs and files, revealing files, and
/// message boxes.</summary>
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

    /// <summary>Message boxes without a window, such as for an error before the first window opens.</summary>
    /// <remarks>
    /// <para>
    /// A box asks for it with <see cref="OwnerPolicy.PreferOwner"/>; with <see cref="OwnerPolicy.RequireOwner"/>, the
    /// default, it reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as the application has no window to
    /// own it. The box is modal for the application rather than for a window, and otherwise follows
    /// <see cref="IDesktopMessageBoxes.ShowAsync"/>. Its capability is <see cref="PlatformCapabilityIds.MessageBoxes"/>,
    /// which describes a <see cref="OwnerPolicy.PreferOwner"/> box.
    /// </para>
    /// <para>
    /// Runic Desktop shows a task dialog on Windows, an application-modal alert on macOS, which needs the main thread (the
    /// Desktop event loop, or a call on the main thread), and on Linux a dialog of the GTK toolkit the process runs, once
    /// its loop runs: from the start of a GTK 4 event loop, and with GTK 3 once a window opened. Without a running toolkit
    /// Linux reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </para>
    /// </remarks>
    /// <example><code>
    /// await platform.MessageBoxes.ShowAsync(new DesktopMessageBox("The settings cannot be read.")
    /// {
    ///     Kind = DesktopMessageBoxKind.Error, OwnerPolicy = OwnerPolicy.PreferOwner,
    /// });
    /// </code></example>
    IDesktopMessageBoxes MessageBoxes { get; }
}

/// <summary>Application platform services for hosts that cannot provide them.</summary>
public static class ApplicationPlatformServices
{
    /// <summary>The capability identifiers the application's platform services report.</summary>
    public static ImmutableArray<string> CapabilityIds { get; } =
        [PlatformCapabilityIds.FilesLaunch, PlatformCapabilityIds.UrisLaunch, PlatformCapabilityIds.MessageBoxes];

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
        public IDesktopMessageBoxes MessageBoxes { get; } = DesktopMessageBoxes.Unavailable(reason);
        public CapabilitySnapshot GetSnapshot() => _snapshot;
    }
}
