using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

/// <summary>Immutable application identity shared by toolkit-independent portal services.</summary>
/// <remarks>Configure once at application composition. Each service retains its own connection and lifetime;
/// this object requires no disposal. A supplied host ID requires a matching installed desktop entry.
/// Omitting the ID retains desktop-inferred identity for development or externally identified applications.</remarks>
public sealed class PortalApplication
{
    /// <summary>The installed reverse-DNS application identifier, or null for desktop-inferred identity.</summary>
    public string? ApplicationId { get; }
    private readonly Action<PortalDiagnostic>? _diagnosticSink;

    /// <summary>Creates one identity configuration to use across all portal services.</summary>
    public PortalApplication(string? applicationId = null, Action<PortalDiagnostic>? diagnosticSink = null)
    {
        if (applicationId is not null && (applicationId.Length > 255 || !applicationId.Contains('.')
            || applicationId.Split('.').Any(part => part.Length == 0 || char.IsAsciiDigit(part[0])
                || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))))
            throw new ArgumentException("Use the installed application's reverse-DNS D-Bus identifier.", nameof(applicationId));
        ApplicationId = applicationId; _diagnosticSink = diagnosticSink;
    }

    /// <summary>Creates operation-scoped idle power inhibition associated with a presentation owner.</summary>
    public IDesktopInhibition CreateInhibition(IPortalWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return new PortalInhibition(owner, this); }
    /// <summary>Creates idle power inhibition for an application without a native presentation, or one whose
    /// operations outlive their windows.</summary>
    /// <remarks>Each request sends the empty parent window identifier, which the Inhibit portal accepts, so the request
    /// is not tied to a window. Use <see cref="CreateInhibition"/> to associate requests with a presentation.</remarks>
    public IDesktopInhibition CreateUnparentedInhibition() => new PortalInhibition(new PortalPlatformProvider.UnparentedOwner(), this);
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
    /// <summary>Creates file handoffs for a presentation owner.</summary>
    public IDesktopFileLauncher CreateFileLauncher(IPortalWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return new PortalFileLauncher(owner, this); }
    /// <summary>Creates portal file dialogs for a presentation owner.</summary>
    public IPickerBackend CreateFileDialogs(IPortalWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new PortalFilePicker(owner, new PortalTransport(application: this), _diagnosticSink));
    }
    /// <summary>Creates explicitly unparented file dialogs for a hostless application.</summary>
    public IPickerBackend CreateUnparentedFileDialogs() => CreateFileDialogs(new PortalPlatformProvider.UnparentedOwner());
    /// <summary>Asks the desktop to open an HTTP, HTTPS or mail URI for this presentation.</summary>
    public ValueTask<PlatformResult<PlatformUnit>> OpenUriAsync(IPortalWindowOwner owner, Uri uri, CancellationToken cancellationToken = default) =>
        PortalPlatformProvider.OpenUriCoreAsync(owner, uri, this, cancellationToken);

    internal void Diagnose(string code, string message, string remedy)
    {
        try { _diagnosticSink?.Invoke(new(code, message, remedy)); }
        catch { /* Diagnostic observers cannot replace platform outcomes. */ }
    }
}
