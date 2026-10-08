namespace Runic.Desktop.Gtk4;

/// <summary>Applies the GTK 4 and WebKitGTK 6 Desktop profile.</summary>
public static class Gtk4DesktopHostOptionsExtensions
{
    /// <summary>Selects GTK 4 and WebKitGTK 6 for embedded Linux windows.</summary>
    /// <remarks>
    /// Start the host with <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>, which
    /// runs GTK on the Linux main thread. Sets <see cref="LinuxDesktopOptions.EmbeddedBackend"/> to <see cref="LinuxEmbeddedBackend.Gtk4WebKit6"/> and,
    /// on Linux, <see cref="DesktopHostOptions.WindowHostFactory"/> to a <see cref="Gtk4WindowHostFactory"/>, so the
    /// two can no longer disagree. On Windows and macOS the platform's own embedded host stays in use; only the Linux
    /// selection is recorded.
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

    /// <summary>Selects GTK 4 and WebKitGTK 6 for embedded Linux windows with an installed GTK application ID.</summary>
    /// <remarks>Like <see cref="WithGtk4(DesktopHostOptions)"/>; the factory runs GTK with <paramref name="applicationId"/>.</remarks>
    /// <param name="options">The host options to copy.</param>
    /// <param name="applicationId">The reverse-DNS application ID. Inside Flatpak it must match the package ID.</param>
    /// <returns>A copy of <paramref name="options"/> with the GTK 4 profile applied.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="applicationId"/> is not a valid reverse-DNS ID (checked on every OS), or <paramref name="options"/>
    /// already has a different window host factory.
    /// </exception>
    public static DesktopHostOptions WithGtk4(this DesktopHostOptions options, string applicationId)
    {
        ValidateApplicationId(applicationId, nameof(applicationId));
        var configured = options.WithGtk4();
        return OperatingSystem.IsLinux()
            ? configured with { WindowHostFactory = new Gtk4WindowHostFactory { ApplicationId = applicationId } }
            : configured;
    }

    // GLib's g_application_id_is_valid rules, checked without loading GLib so every OS validates the same way:
    // at most 255 characters, two or more non-empty dot-separated elements of [A-Za-z0-9_-], none starting
    // with a digit.
    internal static void ValidateApplicationId(string applicationId, string? parameterName = "value")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId, parameterName);
        var elements = applicationId.Split('.');
        if (applicationId.Length > 255 || elements.Length < 2 || elements.Any(static element => element.Length == 0
            || char.IsAsciiDigit(element[0])
            || !element.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        {
            throw new ArgumentException(
                $"'{applicationId}' is not a valid reverse-DNS GTK application ID, such as org.example.App.", parameterName);
        }
    }
}
