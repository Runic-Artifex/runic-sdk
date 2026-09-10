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

    /// <summary>Creates application-scoped appearance preferences.</summary>
    public IDesktopSettings CreateSettings() => new PortalDesktopSettings(application: this);
    /// <summary>Creates application-scoped notifications; only one service may own this application bus name.</summary>
    public IDesktopNotifications CreateNotifications() => new PortalNotifications(application: this);
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
    public ValueTask<PlatformResult<Unit>> OpenUriAsync(IPortalWindowOwner owner, Uri uri, CancellationToken cancellationToken = default) =>
        PortalPlatformProvider.OpenUriCoreAsync(owner, uri, this, cancellationToken);

    internal void Diagnose(string code, string message, string remedy)
    {
        try { _diagnosticSink?.Invoke(new(code, message, remedy)); }
        catch { /* Diagnostic observers cannot replace platform outcomes. */ }
    }
}
