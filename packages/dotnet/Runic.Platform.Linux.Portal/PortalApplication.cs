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
    /// <summary>Creates the computer's network status from the NetworkMonitor portal, or from NetworkManager where no
    /// portal answers.</summary>
    /// <remarks>
    /// <para>
    /// xdg-desktop-portal implements <c>org.freedesktop.portal.NetworkMonitor</c> itself, for sandboxed and unsandboxed
    /// applications alike: <c>GetStatus</c> from version 3 (<c>available</c>, <c>metered</c> and <c>connectivity</c>, whose
    /// local, limited, captive-portal and full levels map to <see cref="DesktopNetworkConnectivity"/>), the separate
    /// getters before, and its <c>changed</c> signal. Without a portal, such as under a minimal window manager, it reads
    /// NetworkManager's <c>State</c>, <c>Connectivity</c> and <c>Metered</c> on the system bus and follows their
    /// <c>PropertiesChanged</c>. When neither answers, reads report <see cref="PlatformUnavailableReason.BackendUnavailable"/>
    /// and the diagnostic <c>network-monitor-unavailable</c>.
    /// </para>
    /// <para>A watch reports <see cref="PlatformResult{T}.Unavailable"/> when the portal or NetworkManager stops and a
    /// fresh state once one answers again. It needs no window and connects to nothing until the first request.</para>
    /// </remarks>
    public IDesktopNetworkStatus CreateNetworkStatus() => new NetworkStatusService(new PortalNetworkStatus(this));
    /// <summary>Creates the registration of this application's URL schemes and file types with the desktop, for the
    /// current user.</summary>
    /// <remarks>
    /// <para>Outside a sandbox it writes the desktop entry <c>$XDG_DATA_HOME/applications/&lt;application ID&gt;.desktop</c>
    /// (<c>Exec="&lt;executable&gt;" %U</c>, <c>MimeType=</c> with <c>x-scheme-handler/&lt;scheme&gt;</c> and the file
    /// types, <c>X-Runic-Registered=true</c>) and, for a file type whose extension the shared MIME-info database does not
    /// give its <see cref="DesktopFileType.MimeType"/>, a definition in <c>$XDG_DATA_HOME/mime/packages/&lt;application ID&gt;.xml</c>,
    /// then runs <c>update-mime-database</c> and <c>update-desktop-database</c> where they are installed. A registration
    /// replaces the application's earlier one. A file type without a MIME type uses the database's type for its
    /// extension, <see cref="PlatformFailureCode.InvalidData"/> when it has none.</para>
    /// <para>An entry of this ID that the application's package installed in <c>XDG_DATA_DIRS</c>, or one in
    /// <c>XDG_DATA_HOME</c> without the marker, is <see cref="PlatformFailureCode.Conflict"/> and reports
    /// <c>open-registration-foreign-entry</c>; nothing is written. Inside Flatpak or Snap, whose manifest exports the
    /// entry and MIME definitions, every request reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// It never sets a default (<c>mimeapps.list</c>).</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This object has no <see cref="ApplicationId"/>, which names the entry.</exception>
    public IDesktopOpenRegistration CreateOpenRegistration() => new LinuxOpenRegistration(this, ApplicationId
        ?? throw new InvalidOperationException("Create the PortalApplication with the application's ID, which names its desktop entry."));

    /// <summary>
    /// Serves D-Bus activation of the running instance: owns the application ID on the session bus and answers
    /// <c>org.freedesktop.Application</c> at the object path derived from it, as a desktop entry with
    /// <c>DBusActivatable=true</c> requires.
    /// </summary>
    /// <param name="instance">The running instance from <c>ApplicationInstance.ClaimAsync</c>. Each activation arrives as
    /// one of its launches: <c>Activate</c> without arguments, <c>Open</c> with the URIs as arguments, which open
    /// requests created for the instance take, and <c>ActivateAction</c> with the <c>Exec</c> arguments of the installed
    /// entry's <c>[Desktop Action &lt;name&gt;]</c> (the action's parameter is not used). The <c>activation-token</c> or
    /// <c>desktop-startup-id</c> becomes the launch's <see cref="ApplicationLaunch.PlatformContext"/>.</param>
    /// <param name="cancellationToken">Cancels before the name is owned.</param>
    /// <returns>The export, whose disposal releases the name; <see cref="PlatformFailureCode.ResourceBusy"/>, with
    /// <c>dbus-activation-name-taken</c>, when another process owns the application ID, and
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> without a session bus.</returns>
    /// <remarks>
    /// Call it once the instance is claimed, before the first window: the desktop starts a D-Bus-activatable
    /// application through its D-Bus service file and waits for the name. A call that comes while the instance is
    /// ending fails back to the caller. Notifications of this object share the name and the object path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This object has no <see cref="ApplicationId"/>.</exception>
    public ValueTask<PlatformResult<IAsyncDisposable>> ExportApplicationAsync(IApplicationInstance instance, CancellationToken cancellationToken = default) =>
        ExportApplicationAsync(instance, null, DesktopApplicationsOptions.FromProcess(), cancellationToken);

    internal async ValueTask<PlatformResult<IAsyncDisposable>> ExportApplicationAsync(IApplicationInstance instance, string? address,
        DesktopApplicationsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string id = ApplicationId ?? throw new InvalidOperationException("Create the PortalApplication with the application's ID, which names its bus name.");
        cancellationToken.ThrowIfCancellationRequested();
        Linux.DBus.DBusConnection connection;
        try { connection = (await PortalBus.Session(address, PortalConnection.RegisteredIdentity(this)).ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection; }
        catch (NativeBackendUnavailableException) { return new PlatformResult<IAsyncDisposable>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (TimeoutException) { return new PlatformResult<IAsyncDisposable>.Failed(PlatformFailureCode.IOError); }
        ApplicationBusObject shared;
        try { shared = await ApplicationBusObject.AcquireAsync(connection, id, cancellationToken).ConfigureAwait(false); }
        catch (ApplicationBusObject.NameTakenException)
        {
            Diagnose("dbus-activation-name-taken", $"Another process owns {id} on the session bus.",
                "Only the running instance serves D-Bus activation: claim the instance first, and export it once.");
            return new PlatformResult<IAsyncDisposable>.Failed(PlatformFailureCode.ResourceBusy);
        }
        catch (Exception error) when (error is InvalidOperationException or Linux.DBus.DBusException or TimeoutException)
        {
            return new PlatformResult<IAsyncDisposable>.Failed(PlatformFailureCode.ResourceBusy);
        }
        if (shared.Activation is not null)
        {
            await shared.ReleaseAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The application's D-Bus activation is already exported; dispose that export first.");
        }
        shared.Activation = new ApplicationActivation(id, instance, options);
        return new PlatformResult<IAsyncDisposable>.Success(new ApplicationExport(shared));
    }

    private sealed class ApplicationExport(ApplicationBusObject shared) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            shared.Activation = null;
            await shared.ReleaseAsync().ConfigureAwait(false);
        }
    }

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
