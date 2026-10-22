using System.Runtime.Versioning;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

/// <summary>The Linux provider of the application-scoped Runic.Platform services: XDG desktop portals, D-Bus services and
/// freedesktop.org specifications, with one application identity and diagnostic sink for all of them.</summary>
/// <remarks>
/// <para>Configure once at application composition. Each service retains its own connection and lifetime;
/// this object requires no disposal. A supplied host ID requires a matching installed desktop entry.
/// Omitting the ID retains desktop-inferred identity for development or externally identified applications.</para>
/// <para>Windows and macOS have static providers (<c>WindowsPlatformProvider</c>, <c>MacOSPlatformProvider</c>) because
/// their identity comes from the process: the AppUserModelID or the bundle. On Linux the portals identify the application
/// by the desktop ID it registers once per connection, and report setup problems to the sink, so both live on this
/// object.</para>
/// <para>Window services (file dialogs, the clipboard and file handoff) belong to a window: Runic's hosts create them
/// for each window, such as <c>DesktopPlatformServices.ForWindow</c> of Runic.Application.Views.Desktop.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PortalApplication
{
    private static PortalApplication? s_inferred;
    // Desktop-inferred identity without a sink, for the window services the hosts create.
    internal static PortalApplication Inferred => s_inferred ??= new();

    /// <summary>The installed reverse-DNS application identifier, or null for desktop-inferred identity.</summary>
    public string? ApplicationId { get; }
    private readonly Action<PortalDiagnostic>? _diagnosticSink;

    /// <summary>Creates one identity configuration to use across all portal services.</summary>
    /// <param name="applicationId">The installed reverse-DNS desktop ID, or null for desktop-inferred identity.</param>
    /// <param name="diagnosticSink">Receives setup messages for logs; see <see cref="PortalDiagnostic"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is not a reverse-DNS D-Bus identifier.</exception>
    /// <exception cref="PlatformNotSupportedException">The process does not run on Linux.</exception>
    public PortalApplication(string? applicationId = null, Action<PortalDiagnostic>? diagnosticSink = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The desktop portals are a Linux service.");
        if (applicationId is not null && (applicationId.Length > 255 || !applicationId.Contains('.')
            || applicationId.Split('.').Any(part => part.Length == 0 || char.IsAsciiDigit(part[0])
                || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))))
            throw new ArgumentException("Use the installed application's reverse-DNS D-Bus identifier.", nameof(applicationId));
        ApplicationId = applicationId; _diagnosticSink = diagnosticSink;
    }

    // Inhibition associated with a window, for a host.
    internal IDesktopInhibition CreateInhibition(IPortalWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return new PortalInhibition(owner, this); }
    /// <summary>Creates application-scoped idle power inhibition, as the Windows and macOS providers do.</summary>
    /// <remarks>Each request sends the empty parent window identifier, which the Inhibit portal accepts, so the request
    /// belongs to the application rather than to a window and works without one.</remarks>
    public IDesktopInhibition CreateInhibition() => new PortalInhibition(new PortalPlatformProvider.UnparentedOwner(), this);
    /// <summary>Creates application-scoped appearance preferences.</summary>
    public IDesktopSettings CreateSettings() => new PortalDesktopSettings(application: this);
    /// <summary>Creates application-scoped notifications; only one service may own this application bus name.</summary>
    public IDesktopNotifications CreateNotifications() => new PortalNotifications(application: this);
    /// <summary>Creates a status icon with a menu, registered with the desktop's StatusNotifierWatcher.</summary>
    /// <remarks>Its <c>Id</c> is this application ID. Status icons have no portal: a sandbox must allow owning
    /// <c>org.kde.StatusNotifierItem-*</c> names and talking to <c>org.kde.StatusNotifierWatcher</c>.</remarks>
    public IDesktopStatusIcon CreateStatusIcon() => new StatusNotifierIcon(application: this);
    /// <summary>Creates login startup for this application. Nothing is registered until it is enabled.</summary>
    /// <remarks>Outside a sandbox it writes <c>~/.config/autostart/&lt;application ID&gt;.desktop</c>, so it needs the
    /// application ID; inside Flatpak or Snap it asks the Background portal, which may ask the user.</remarks>
    public IDesktopLoginStartup CreateLoginStartup() => new LinuxLoginStartup(this);
    /// <summary>Creates secret storage in the desktop's Secret Service, such as GNOME Keyring or KeePassXC.</summary>
    /// <remarks>It needs no window and connects to nothing until the first request. Inside Flatpak or Snap the sandbox
    /// must allow talking to <c>org.freedesktop.secrets</c>; otherwise requests report unavailable.</remarks>
    public IDesktopSecretStore CreateSecretStore() => new SecretServiceStore(application: this);
    /// <summary>Creates the user's trash, following the freedesktop.org Trash specification.</summary>
    /// <remarks>It needs no window. Outside a sandbox it moves items into <c>$XDG_DATA_HOME/Trash</c>, or into the trash
    /// at the top of the item's mount; inside Flatpak or Snap it asks the Trash portal.</remarks>
    public IDesktopTrash CreateTrash() => new LinuxTrash(application: this);

    /// <summary>Creates the desktop's application database: content types, installed applications, default applications
    /// and icons, read from the freedesktop.org files.</summary>
    /// <remarks>It needs no window and reads the XDG directories from the environment of this process when created. Only
    /// the icon theme lookup and D-Bus-activatable launches use the session bus. Inside Flatpak or Snap the host's
    /// applications are out of reach; content types and icons come from the sandbox's own data.</remarks>
    public IDesktopApplications CreateApplications() => new LinuxDesktopApplications(DesktopApplicationsOptions.FromProcess(), this);
    /// <summary>Creates thumbnails from the freedesktop.org thumbnail cache, and from the session's thumbnailer on a miss.</summary>
    /// <remarks>It needs no window and reads the XDG directories from the environment of this process when created. On a
    /// cache miss it asks <c>org.freedesktop.thumbnails.Thumbnailer1</c>, such as Tumbler, on the session bus; GNOME and KDE
    /// run none, so there only thumbnails their file managers cached are returned. Inside Flatpak the sandbox must allow
    /// reading the host's cache (<c>--filesystem=xdg-cache/thumbnails:ro</c>) and talking to the thumbnailer.</remarks>
    public IDesktopThumbnails CreateThumbnails() => new LinuxThumbnails(LinuxThumbnailOptions.FromProcess(), this);
    /// <summary>Creates the desktop's storage volumes from UDisks2 on the system bus.</summary>
    /// <remarks>UDisks2 asks Polkit for authorization itself and may show a prompt. The volumes need no portal and no
    /// window; a sandbox must allow talking to <c>org.freedesktop.UDisks2</c> on the system bus. Creating them
    /// connects to nothing; the first request does.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "A factory of the provider instance, as the other services are; the volumes need no identity.")]
    public IDesktopVolumes CreateVolumes() => new UDisksVolumes();
    // File and URI handoffs for a window, for a host.
    internal IDesktopFileLauncher CreateFileLauncher(IPortalWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return new PortalFileLauncher(owner, this); }
    // Portal file dialogs for a window, for a host.
    internal IPickerBackend CreateFileDialogs(IPortalWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new PortalFilePicker(owner, new PortalTransport(application: this), _diagnosticSink));
    }
    /// <summary>Creates explicitly unparented file dialogs for an application without a window.</summary>
    /// <remarks>Linux only: the portal may show such a dialog without a parent. Windows in a Runic host get dialogs owned
    /// by the window from the host instead.</remarks>
    public IPickerBackend CreateUnparentedFileDialogs() => CreateFileDialogs(new PortalPlatformProvider.UnparentedOwner());

    internal void Diagnose(string code, string message, string remedy)
    {
        try { _diagnosticSink?.Invoke(PortalDiagnostic.Create(code, message, remedy)); }
        catch { /* Diagnostic observers cannot replace platform outcomes. */ }
    }
}
