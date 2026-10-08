namespace Runic.Desktop.Gtk4;

/// <summary>Applies the GTK 4 and WebKitGTK 6 Desktop profile.</summary>
public static class Gtk4DesktopHostOptionsExtensions
{
    /// <summary>Selects GTK 4 and WebKitGTK 6 for embedded Linux windows.</summary>
    /// <remarks>
    /// Sets <see cref="LinuxDesktopOptions.EmbeddedBackend"/> to <see cref="LinuxEmbeddedBackend.Gtk4WebKit6"/> and,
    /// on Linux, <see cref="DesktopHostOptions.WindowHostFactory"/> to a <see cref="Gtk4WindowHostFactory"/>, so the
    /// two can no longer disagree. On Windows and macOS the platform's own embedded host stays in use; only the Linux
    /// selection is recorded. Run the host inside <see cref="Gtk4Application.Run(Func{Task{int}})"/> on Linux.
    /// </remarks>
    /// <param name="options">The host options to copy.</param>
    /// <returns>A copy of <paramref name="options"/> with the GTK 4 profile applied.</returns>
    /// <exception cref="ArgumentException"><paramref name="options"/> already has a different window host factory.</exception>
    public static DesktopHostOptions WithGtk4(this DesktopHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Linux, nameof(options));
        if (options.WindowHostFactory is { } factory and not Gtk4WindowHostFactory)
        {
            throw new ArgumentException(
                $"WindowHostFactory is already set to {factory.GetType().Name}; the GTK 4 profile supplies its own factory.",
                nameof(options));
        }

        var configured = options with { Linux = options.Linux with { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 } };
        if (OperatingSystem.IsLinux() && configured.WindowHostFactory is null)
        {
            configured = configured with { WindowHostFactory = new Gtk4WindowHostFactory() };
        }
        return configured;
    }
}
